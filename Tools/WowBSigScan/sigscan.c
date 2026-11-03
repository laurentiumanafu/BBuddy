// WowBSigScan v3 - apply byte signatures from Winkr-Public to wowb.exe.
//
// History of this tool, because each version exists for a reason:
//  v1 aborted on the first partial 4 MB read (missed its own control).
//  v2 scanned the PE section table but the live .text is only partly resident:
//     two runs minutes apart gave unreadable=0x36AA43C then 0x3A0143C, control
//     copies 9 then 5, and HANDLE_TERRAIN_CLICK flipped UNIQUE -> NOT FOUND.
//     So process-mode results are racy by nature and NOT FOUND there is weak.
//  v3 adds --file: scan a decoded image on disk (complete, deterministic, no
//     target process), and fixes an absolute/relative offset bug that made the
//     process-mode protection histogram print readable=0.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include <math.h>
#include "sigtable.h"
#include "scancore.h"
#include "codecheck.h"

#define MAX_USER 0x00007FFFFFFFFFFFULL
#define HEALTH_RVA 0x1D63190ULL
#define MAX_MATCHES 32
#define MAX_SECTIONS 96
#define PAGE 4096u
#define MAX_SECTION_BYTES (192u*1024u*1024u)
#define MAX_FILE_BYTES (1536ull*1024ull*1024ull)

// 16 confirmed bytes at wowb.exe +0x1D63190. NOT unique on this build (5-9 known
// copies), so it identifies the build by presence, never by uniqueness.
static const unsigned char kControl[16]={0x48,0x8b,0xc4,0x55,0x41,0x57,0x48,0x8d,0x68,0xa1,0x48,0x81,0xec,0xb8,0,0};
static const char kControlMask[]="xxxxxxxxxxxxxxxx";

typedef struct { char name[9]; uint32_t va, vsize, raw, chars; } section_t;
typedef struct { uint32_t rva, fileOff, len; int exec; char name[9]; } region_t;

static uint64_t ui(const void *p) { return (uint64_t)(uintptr_t)p; }
static uint32_t le32(const unsigned char *p) { uint32_t v; memcpy(&v,p,4); return v; }
static int read_exact(HANDLE p, uint64_t addr, void *out, SIZE_T n) {
    SIZE_T got=0;
    return n && addr>=0x10000 && addr<MAX_USER && n<=MAX_USER-addr &&
        ReadProcessMemory(p,(void*)(uintptr_t)addr,out,n,&got) && got==n;
}
static uint64_t filetime64(FILETIME f) { return ((uint64_t)f.dwHighDateTime<<32)|f.dwLowDateTime; }
static DWORD find_pid(void) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e);
    DWORD pid=0,count=0;
    if (Process32FirstW(snap,&e)) do {
        if (_wcsicmp(e.szExeFile,L"wowb.exe")==0) { pid=e.th32ProcessID; count++; }
    } while (Process32NextW(snap,&e));
    CloseHandle(snap); return count==1 ? pid : 0;
}
static uint64_t module_base(DWORD pid, DWORD *size) {
    HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
    if (snap==INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W e; ZeroMemory(&e,sizeof(e)); e.dwSize=sizeof(e); uint64_t found=0;
    if (Module32FirstW(snap,&e)) do {
        if (_wcsicmp(e.szModule,L"wowb.exe")==0) { found=ui(e.modBaseAddr); if (size) *size=e.modBaseSize; break; }
    } while (Module32NextW(snap,&e));
    CloseHandle(snap); return found;
}
static int valid_identity(HANDLE process, uint64_t imageBase) {
    unsigned char dos[64], pe[6], sample[16];
    static const unsigned char expected[16]={0x48,0x8b,0xc4,0x55,0x41,0x57,0x48,0x8d,0x68,0xa1,0x48,0x81,0xec,0xb8,0,0};
    if (!read_exact(process,imageBase,dos,sizeof(dos)) || dos[0]!='M' || dos[1]!='Z') return 0;
    uint32_t peoff=le32(dos+0x3c); if (peoff<64 || peoff>0x100000) return 0;
    if (!read_exact(process,imageBase+peoff,pe,6) || memcmp(pe,"PE\0\0",4) || pe[4]!=0x64 || pe[5]!=0x86) return 0;
    return read_exact(process,imageBase+HEALTH_RVA,sample,16) && memcmp(sample,expected,16)==0;
}

static const region_t* region_lookup(const region_t *r, unsigned n, uint64_t rva) {
    for (unsigned i=0;i<n;i++)
        if (rva>=r[i].rva && rva<r[i].rva+(uint64_t)r[i].len) return &r[i];
    return NULL;
}

// Every result set is kept per pattern so the two modes share reporting.
typedef struct {
    unsigned *counts; unsigned *truncated; sc_hit_t *hits; unsigned npat;
} results_t;

static int results_init(results_t *r) {
    r->npat=g_sigCount+1;                       // +1 = the control pattern
    r->counts=(unsigned*)calloc(r->npat,sizeof(unsigned));
    r->truncated=(unsigned*)calloc(r->npat,sizeof(unsigned));
    r->hits=(sc_hit_t*)calloc((size_t)r->npat*MAX_MATCHES,sizeof(sc_hit_t));
    return r->counts && r->truncated && r->hits;
}
static void results_free(results_t *r) { free(r->counts); free(r->truncated); free(r->hits); }

// Watch mode rescans, so the same site is found again on every pass. Without
// this, a unique function would be reported AMBIGUOUS purely because it was seen
// twice - the tool would invent a duplicate out of its own repetition.
static void results_compact(results_t *r) {
    for (unsigned p=0;p<r->npat;p++) {
        unsigned n = r->counts[p] > MAX_MATCHES ? MAX_MATCHES : r->counts[p];
        r->counts[p]=sc_dedupe_hits(r->hits + (size_t)p*MAX_MATCHES, n);
    }
}
// Accumulate into each pattern's slot at its CURRENT count. Writing from index 0
// on every pass was a bug: after two passes the count became 2 while only slot 0
// had ever been written, so the report printed a second, all-zero "hit" at rva 0
// and called a unique function AMBIGUOUS. Four patterns showed that phantom in
// the first v5 field report.
static void results_scan(results_t *r, const unsigned char *buf, unsigned len,
                         const unsigned char *valid, uint64_t baseRva, unsigned minLen) {
    int trunc=0;
    sc_scan_into(buf,len,valid,kControl,kControlMask,16,baseRva,
                 r->hits+(size_t)g_sigCount*MAX_MATCHES,MAX_MATCHES,
                 &r->counts[g_sigCount],&trunc);
    if (trunc) r->truncated[g_sigCount]=1;
    for (unsigned p=0;p<g_sigCount;p++) {
        const sig_t *g=&g_sigs[p];
        if (g->len<minLen || g->len>len) continue;
        sc_scan_into(buf,len,valid,g->bytes,g->mask,g->len,baseRva,
                     r->hits+(size_t)p*MAX_MATCHES,MAX_MATCHES,
                     &r->counts[p],&trunc);
        if (trunc) r->truncated[p]=1;
    }
}

static void report_results(results_t *r, FILE *log,
                           const region_t *regs, unsigned nregs, int labelHits,
                           double execPct, const unsigned *firstPass, unsigned passes) {
    unsigned found=0, inCode=0, lookalikes=0;
    fprintf(log,"\n=== RESULTS ===\n");
    for (unsigned s=0;s<g_sigCount;s++) {
        const sig_t *g=&g_sigs[s];
        unsigned real=0;
        for (unsigned k=0;k<r->counts[s] && k<MAX_MATCHES;k++) {
            const region_t *rg = labelHits ? region_lookup(regs,nregs,r->hits[(size_t)s*MAX_MATCHES+k].rva) : NULL;
            if (!labelHits || (rg && rg->exec)) real++;
        }
        const char *verdict; char notFound[96];
        if (r->counts[s]==0) {
            // Not "retry in progress": --watch keeps a union of the pages it did
            // read, so a page that was locked stays locked and the retry cannot
            // reach it. The only honest statement is how much was never looked at.
            snprintf(notFound,sizeof(notFound),
                "UNSEEN (only %u%% of executable bytes were readable)",
                (unsigned)(execPct+0.5));
            verdict=notFound;
        }
        else if (real==0) verdict="ONLY IN DATA (lookalikes, not code)";
        else if (real==1) verdict="UNIQUE";
        else verdict="AMBIGUOUS";
        if (r->counts[s]) found++;
        if (real) inCode++;
        if (r->counts[s] && real==0) lookalikes++;
        fprintf(log,"\n%-28s len=%-3u matches=%-3u %s%s\n",g->name,g->len,r->counts[s],verdict,
            r->truncated[s]?" (list truncated)":"");
        if (firstPass && real && firstPass[s]>1)
            fprintf(log,"    (only became readable on pass %u of %u)\n",firstPass[s],passes);
        for (unsigned k=0;k<r->counts[s] && k<MAX_MATCHES;k++) {
            sc_hit_t *h=&r->hits[(size_t)s*MAX_MATCHES+k];
            const region_t *rg = labelHits ? region_lookup(regs,nregs,h->rva) : NULL;
            fprintf(log,"    rva=0x%08llX prev=0x%02X bytes=",(unsigned long long)h->rva,h->prev4);
            for (unsigned b=0;b<12 && b<g->len;b++) fprintf(log,"%02X ",h->at[b]);
            if (rg) fprintf(log," [%s%s]",rg->name,rg->exec?"":", data");
            else if (h->prev4==0xCC || h->prev4==0xC3 || h->prev4==0x00)
                fprintf(log," (preceded by padding/tail: plausible function start)");
            if (labelHits && rg && !rg->exec)
                fprintf(log," <- data section: matches the bytes, is not the function");
            fprintf(log,"\n");
        }
    }
    fprintf(log,"\nsummary: %u of %u patterns matched; %u in executable code; %u only inside data\n",
        found,g_sigCount,inCode,lookalikes);
    if (lookalikes)
        fprintf(log,"A match inside a data section is NOT the function. These patterns are short enough\n"
                    "to occur by chance in compressed or string data, so only executable matches count.\n");
}

// ------------------------------------------------------------ shared reports
static int control_present(results_t *r, uint64_t *where) {
    for (unsigned k=0;k<r->counts[g_sigCount] && k<MAX_MATCHES;k++) {
        uint64_t rva=r->hits[(size_t)g_sigCount*MAX_MATCHES+k].rva;
        if (rva==HEALTH_RVA) { if (where) *where=rva; return 1; }
    }
    return 0;
}
static void report_control(results_t *r, FILE *log, int confirmed) {
    unsigned idx=g_sigCount;
    fprintf(log,"\n=== POSITIVE CONTROL ===\n");
    fprintf(log,"UNITHEALTH_PROLOGUE copies=%u  known site 0x%llX %s\n",
        r->counts[idx],(unsigned long long)HEALTH_RVA,confirmed?"PRESENT":"ABSENT");
    if (!confirmed)
        fprintf(log,"MAPPING NOT CONFIRMED: the known site is not where it should be, so the\n"
                    "offsets below are offsets into this file, NOT image RVAs.\n");
    else
        fprintf(log,"MAPPING CONFIRMED: the known site is where it should be, so these are real RVAs.\n");
}

// --------------------------------------------------------------- file mode
// Structural verdict before any pattern work: a decrypted image has raw data for
// its code. A .text of 1 KiB on disk for 76 MiB of virtual space is a packed or
// encrypted file, and scanning it offline is exactly what the reference notes warn
// about ("entropy 7.96, zero x64 prologues, scores 0 signatures").
static int file_structure_check(const unsigned char *img, size_t sz, unsigned secOff,
                                unsigned nsec, FILE *log) {
    int packed=0;
    fprintf(log,"\n=== STRUCTURE ===\n");
    fprintf(log,"%-8s %12s %12s %8s %8s %10s %10s %s\n",
        "section","virtual","raw","raw/virt","entropy","prologues/MB","int3/MB","note");
    for (unsigned i=0;i<nsec;i++) {
        const unsigned char *sh=img+secOff+i*40;
        char nm[9]; memcpy(nm,sh,8); nm[8]=0;
        uint32_t vsize=le32(sh+8), raw=le32(sh+16), rawsize=le32(sh+20), chars=le32(sh+36);
        if (!vsize && !rawsize) continue;
        double ratio = vsize ? (double)rawsize/(double)vsize : 1.0;
        // Sample up to 1 MB for the content statistics.
        unsigned sample = rawsize<(1u<<20)?rawsize:(1u<<20);
        double h=0; unsigned pr=0, pd=0; double perMb=0;
        if (sample && (uint64_t)raw+sample<=(uint64_t)sz) {
            h=shannon(img+raw,sample);
            code_signals(img+raw,sample,&pr,&pd);
            perMb=(double)pr*1048576.0/(double)sample;
        }
        const char *note="";
        if (vsize>1024*1024 && ratio<0.25) { note="VIRTUAL >> RAW: packed/encrypted"; packed=1; }
        else if (h>7.5 && sample>(64u*1024u)) note="entropy>7.5: encrypted/compressed";
        fprintf(log,"%-8s %12u %12u %8.3f %8.3f %10.0f %10.0f %s%s\n",
            nm,vsize,rawsize,ratio,h,perMb,(double)pd*1048576.0/(double)(sample?sample:1),
            note,(chars&0x20000000)?"  [EXEC]":"");
    }
    if (packed) {
        fprintf(log,"\nVERDICT: this file is NOT a decoded image. Its code sections store far less\n"
                    "on disk than they occupy in memory, which is what an encrypted or packed\n"
                    "client looks like. Pattern results from it mean nothing: any match lands in\n"
                    "the compressed blob, not in real code.\n");
        fprintf(log,"The client decrypts itself in memory, so the honest sources are:\n"
                    "  * the live process (default mode), or\n"
                    "  * a dump taken from the running client, or\n"
                    "  * the actual decoded image Ghidra analysed (check its .text raw size).\n");
    }
    return packed;
}

static int scan_file(const char *path, unsigned minLen, int wantFlat, FILE *log) {
    FILE *f=fopen(path,"rb");
    if (!f) { fprintf(log,"cannot open: %s\n",path); return 1; }
    fseek(f,0,SEEK_END); long sz=ftell(f); rewind(f);
    if (sz<(long)0x1000 || (uint64_t)sz>MAX_FILE_BYTES) {
        fprintf(log,"implausible file size 0x%lX\n",sz); fclose(f); return 1;
    }
    unsigned char *img=(unsigned char*)malloc((size_t)sz);
    if (!img) { fprintf(log,"host allocation failed for 0x%lX bytes\n",sz); fclose(f); return 1; }
    size_t got=fread(img,1,(size_t)sz,f); fclose(f);
    if (got!=(size_t)sz) { fprintf(log,"short read\n"); free(img); return 1; }
    fprintf(log,"file=%s size=0x%lX bytes\n",path,sz);

    unsigned nsec=0, secOff=0;
    int hasPe=0;
    if (img[0]=='M' && img[1]=='Z' && sz>0x100) {
        uint32_t peoff=le32(img+0x3c);
        if (peoff>0x40 && (uint64_t)peoff+0x108<=(uint64_t)sz && memcmp(img+peoff,"PE\0\0",4)==0
            && *(const uint16_t*)(img+peoff+4)==0x8664) {
            nsec=*(const uint16_t*)(img+peoff+6);
            unsigned optSize=*(const uint16_t*)(img+peoff+20);
            secOff=peoff+24+optSize;
            if (nsec>0 && nsec<=MAX_SECTIONS && (uint64_t)secOff+(uint64_t)nsec*40<=(uint64_t)sz) hasPe=1;
            else nsec=0;
        }
    }
    int packed = hasPe ? file_structure_check(img,(size_t)sz,secOff,nsec,log) : 0;

    // Candidate mappings. A confirmed control site decides between them; if none
    // confirms, both are reported and the verdict says the mapping is unknown.
    region_t regsPe[MAX_SECTIONS]; unsigned nPe=0;
    if (hasPe) {
        for (unsigned i=0;i<nsec;i++) {
            const unsigned char *sh=img+secOff+i*40;
            uint32_t vsize=le32(sh+8), va=le32(sh+12), raw=le32(sh+16), rawsize=le32(sh+20), chars=le32(sh+36);
            if (!rawsize || !va) continue;
            if ((uint64_t)raw+rawsize>(uint64_t)sz) continue;
            uint32_t len=(vsize && vsize<rawsize)?vsize:rawsize;
            region_t *r=&regsPe[nPe++];
            r->rva=va; r->fileOff=raw; r->len=len;
            r->exec=(chars&0x20000000)?1:0;
            memcpy(r->name,sh,8); r->name[8]=0;
        }
    }
    region_t regsFlat[1]; unsigned nFlat=0;
    regsFlat[0].rva=0; regsFlat[0].fileOff=0;
    regsFlat[0].len=(uint32_t)((sz>0xFFFFFFFFl)?0xFFFFFFFFl:sz);
    regsFlat[0].exec=1; strcpy(regsFlat[0].name,"flat");
    nFlat=1;

    struct { const char *name; region_t *regs; unsigned n; } cands[2];
    unsigned ncand=0;
    if (nPe) { cands[ncand].name="PE section table"; cands[ncand].regs=regsPe; cands[ncand].n=nPe; ncand++; }
    if (!wantFlat || !nPe) { cands[ncand].name="flat (file offset == RVA)"; cands[ncand].regs=regsFlat; cands[ncand].n=nFlat; ncand++; }
    else { cands[ncand].name="flat (file offset == RVA)"; cands[ncand].regs=regsFlat; cands[ncand].n=nFlat; ncand++; }

    int chosen=-1;
    // Probe each candidate cheaply: does it put the control where the client has it?
    fprintf(log,"\n=== MAPPING CANDIDATES ===\n");
    for (unsigned c=0;c<ncand;c++) {
        results_t probe; if (!results_init(&probe)) { free(img); return 1; }
        for (unsigned i=0;i<cands[c].n;i++) {
            region_t *rg=&cands[c].regs[i];
            if ((uint64_t)rg->fileOff+rg->len>(uint64_t)sz) continue;
            results_scan(&probe,img+rg->fileOff,rg->len,NULL,rg->rva,minLen);
        }
        uint64_t where=0; int ok=control_present(&probe,&where);
        fprintf(log,"  %-26s control cop(ies)=%u  known site %s\n",cands[c].name,
            probe.counts[g_sigCount], ok?"PRESENT":"absent");
        if (ok && chosen<0) chosen=(int)c;
        results_free(&probe);
    }
    if (chosen<0) {
        fprintf(log,"\nNo candidate mapping puts the control at 0x%llX. Reporting the PE mapping,\n",
            (unsigned long long)HEALTH_RVA);
        fprintf(log,"but treat every offset as unverified.\n");
        chosen = nPe ? 0 : (int)(ncand-1);
    } else {
        fprintf(log,"\nusing mapping: %s\n",cands[chosen].name);
    }

    results_t r; if (!results_init(&r)) { free(img); return 1; }
    uint64_t scanned=0;
    for (unsigned i=0;i<cands[chosen].n;i++) {
        region_t *rg=&cands[chosen].regs[i];
        if ((uint64_t)rg->fileOff+rg->len>(uint64_t)sz) continue;
        results_scan(&r,img+rg->fileOff,rg->len,NULL,rg->rva,minLen);
        scanned+=rg->len;
    }
    fprintf(log,"scannedBytes=0x%llX of file 0x%lX (whole file read, no races)\n",
        (unsigned long long)scanned,sz);

    uint64_t site=0; int confirmed=control_present(&r,&site);
    report_control(&r,log,confirmed);
    fprintf(log,"\n=== EXECUTABLE CODE MEASURED ===\n");
    for (unsigned i=0;i<cands[chosen].n;i++) {
        region_t *rg=&cands[chosen].regs[i];
        if (!rg->exec) continue;
        unsigned sample=rg->len<(1u<<20)?rg->len:(1u<<20);
        if (!sample || (uint64_t)rg->fileOff+sample>(uint64_t)sz) continue;
        double h=shannon(img+rg->fileOff,sample);
        unsigned pr=0,pd=0; code_signals(img+rg->fileOff,sample,&pr,&pd);
        fprintf(log,"  %-8s sample=0x%X entropy=%.3f prologues=%.0f/MB int3=%.0f/MB  %s\n",
            rg->name,sample,h,(double)pr*1048576.0/sample,(double)pd*1048576.0/sample,
            (h>7.5 && sample>65536) ? "NOT CODE (encrypted/compressed)" : "looks like code");
    }
    report_results(&r,log,cands[chosen].regs,cands[chosen].n,1,100.0,NULL,1);

    fprintf(log,"\n=== HOW TO USE THIS ===\n");
    if (packed)
        fprintf(log,"This file is packed: fix the source before trusting any line above.\n");
    else if (confirmed)
        fprintf(log,"Mapping confirmed. A UNIQUE executable match is a callable candidate; verify the\n"
                    "body before calling through it.\n");
    else
        fprintf(log,"Mapping unconfirmed: offsets here are file offsets, not RVAs. Do not use them as\n"
                    "addresses. Run the live-process mode instead, or dump from the client.\n");
    results_free(&r); free(img);
    return 0;
}

// --------------------------------------------------------------- process mode
static void protection_notes(HANDLE process, uint64_t base, unsigned size, FILE *log) {
    unsigned long long commit=0, noaccess=0, guard=0, freed=0, other=0;
    unsigned long long ranges=0; uint64_t firstBad=0, lastBad=0;
    MEMORY_BASIC_INFORMATION mbi;
    uint64_t off=0;
    while (off<size) {
        if (VirtualQueryEx(process,(void*)(uintptr_t)(base+off),&mbi,sizeof(mbi))!=sizeof(mbi)) break;
        uint64_t regBase=ui(mbi.BaseAddress);
        uint64_t regSize=mbi.RegionSize?mbi.RegionSize:PAGE;
        uint64_t s=(regBase>base+off)?regBase:(base+off);
        uint64_t e=regBase+regSize;
        if (e>base+(uint64_t)size) e=base+(uint64_t)size;
        unsigned long long len=(e>s)?(e-s):0;
        int bad=0;
        if (mbi.State!=MEM_COMMIT) { freed+=len; bad=1; }
        else if (mbi.Protect & PAGE_GUARD) { guard+=len; bad=1; }
        else if (mbi.Protect & PAGE_NOACCESS) { noaccess+=len; bad=1; }
        else if (mbi.Protect==PAGE_READONLY || mbi.Protect==PAGE_READWRITE ||
                 mbi.Protect==PAGE_WRITECOPY || mbi.Protect==PAGE_EXECUTE_READ ||
                 mbi.Protect==PAGE_EXECUTE_READWRITE || mbi.Protect==PAGE_EXECUTE_WRITECOPY) commit+=len;
        else other+=len;
        if (bad) { ranges++; if (!firstBad) firstBad=s; lastBad=e; }
        if (e<=base+off) break;
        off=e-base;
    }
    fprintf(log,"      protections: readable=0x%llX noaccess=0x%llX guard=0x%llX "
                "notCommitted=0x%llX other=0x%llX unreadableRanges=%llu\n",
        commit,noaccess,guard,freed,other,ranges);
    if (ranges)
        fprintf(log,"      first unreadable at rva=0x%llX, last ends at rva=0x%llX\n",
            (unsigned long long)(firstBad>base?firstBad-base:0),
            (unsigned long long)(lastBad>base?lastBad-base:0));
}

static void read_with_validity(HANDLE process, uint64_t base, unsigned size,
                               unsigned char *buf, unsigned char *valid, unsigned *unreadable) {
    SIZE_T got=0;
    *unreadable=0;
    if (ReadProcessMemory(process,(void*)(uintptr_t)base,buf,size,&got) && got==size) {
        memset(valid,1,size); return;
    }
    for (unsigned off=0; off<size; off+=PAGE) {
        unsigned chunk=(size-off<PAGE)?(size-off):PAGE;
        SIZE_T g=0;
        int ok = ReadProcessMemory(process,(void*)(uintptr_t)(base+off),buf+off,chunk,&g) && g==chunk;
        memset(valid+off, ok?1:0, chunk);
        if (!ok) { memset(buf+off,0,chunk); (*unreadable)+=chunk; }
    }
}

// Watch mode, second and later passes: only pages that were NOT readable before
// are re-read. The client decrypts code as it executes, so a page that was
// PAGE_NOACCESS on pass 1 may be ordinary code two minutes later. Re-reading all
// 76 MB every five seconds is what made the first version of --watch unusable; a
// failed ReadProcessMemory is cheap, but scanning 76 MB x 19 patterns is not.
// Each newly readable page is scanned with an 80 byte lead-in from the page
// before it, so a match that starts a few bytes before the boundary is still
// found (the longest pattern here is 66 bytes).
static unsigned incremental_pass(HANDLE process, uint64_t imageBase, section_t *secs, unsigned nsec,
                                 unsigned char *done, unsigned pageCount,
                                 unsigned minLen, results_t *r) {
    static unsigned char pageBuf[PAGE], lead[80], buf[PAGE+sizeof(lead)];
    unsigned newPages=0;
    for (unsigned i=0;i<nsec;i++) {
        section_t *s=&secs[i];
        if (s->vsize<16 || s->va==0 || s->vsize>MAX_SECTION_BYTES) continue;
        if (imageBase+s->va+s->vsize>MAX_USER) continue;
        for (unsigned off=0; off<s->vsize; off+=PAGE) {
            unsigned chunk=(s->vsize-off<PAGE)?(s->vsize-off):PAGE;
            uint64_t rva=(uint64_t)s->va+off;
            unsigned pg=(unsigned)(rva/PAGE);
            if (pg>=pageCount || done[pg]) continue;
            SIZE_T got=0;
            if (!ReadProcessMemory(process,(void*)(uintptr_t)(imageBase+rva),pageBuf,chunk,&got) || got!=chunk)
                continue;                       // still unreadable: try again next pass
            done[pg]=1;
            newPages++;
            unsigned leadLen=0;
            if (rva>=sizeof(lead)) {
                uint64_t from=rva-sizeof(lead);
                if (from/PAGE<pageCount && done[(unsigned)(from/PAGE)]) {
                    SIZE_T g2=0;
                    if (ReadProcessMemory(process,(void*)(uintptr_t)(imageBase+from),lead,sizeof(lead),&g2)
                        && g2==sizeof(lead)) leadLen=(unsigned)sizeof(lead);
                }
            }
            memcpy(buf,lead,leadLen);
            memcpy(buf+leadLen,pageBuf,chunk);
            results_scan(r,buf,leadLen+chunk,NULL,rva-leadLen,minLen);
        }
    }
    return newPages;
}

// How many executable bytes have been read at least once, so "--watch" can say
// whether the extra passes are buying anything.
static unsigned long long exec_covered(const unsigned char *done, unsigned pageCount,
                                       section_t *secs, unsigned nsec) {
    unsigned long long total=0;
    for (unsigned i=0;i<nsec;i++) {
        section_t *s=&secs[i];
        if (!(s->chars&0x20000000) || s->vsize<16 || s->va==0 || s->vsize>MAX_SECTION_BYTES) continue;
        for (unsigned off=0; off<s->vsize; off+=PAGE) {
            unsigned pg=(unsigned)(((uint64_t)s->va+off)/PAGE);
            if (pg>=pageCount || !done[pg]) continue;
            total += (s->vsize-off<PAGE)?(s->vsize-off):PAGE;
        }
    }
    return total;
}

static int scan_process(DWORD pid, unsigned minLen, unsigned watchSeconds, FILE *log) {
    int result=1; HANDLE process=NULL;
    section_t *secs=NULL; unsigned char *buf=NULL, *valid=NULL, hdr[PAGE];
    region_t *regs=NULL; unsigned nregs=0;
    results_t r; memset(&r,0,sizeof(r));
    fprintf(log,"MODE: live process. .text on this client is only partly readable from outside,\n");
    fprintf(log,"so a NOT FOUND here is weak evidence. --file is only trustworthy on a decrypted\n");
    fprintf(log,"image; check its structure section before believing it.\n\n");

    if (!pid) pid=find_pid();
    if (!pid) { fprintf(log,"Expected exactly one running wowb.exe; pass --pid PID.\n"); return 1; }
    process=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid);
    if (!process) { fprintf(log,"OpenProcess failed=%lu\n",(unsigned long)GetLastError()); return 1; }
    if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) { fprintf(log,"Not a live process.\n"); goto done; }
    FILETIME create,exitT,kernel,user;
    if (!GetProcessTimes(process,&create,&exitT,&kernel,&user)) { fprintf(log,"GetProcessTimes failed\n"); goto done; }
    DWORD imageSize=0; uint64_t imageBase=module_base(pid,&imageSize);
    if (!imageBase || imageSize<HEALTH_RVA+16) { fprintf(log,"wowb.exe module not found.\n"); goto done; }
    if (!valid_identity(process,imageBase)) { fprintf(log,"Identity check failed.\n"); goto done; }
    fprintf(log,"pid=%lu creation=0x%llX imageBase=0x%llX imageSize=0x%lX\n",(unsigned long)pid,
        (unsigned long long)filetime64(create),(unsigned long long)imageBase,(unsigned long)imageSize);

    if (!read_exact(process,imageBase,hdr,sizeof(hdr))) { fprintf(log,"PE headers unreadable\n"); goto done; }
    uint32_t peoff=le32(hdr+0x3c);
    if (peoff+0x108>sizeof(hdr)) { fprintf(log,"PE header beyond first page\n"); goto done; }
    const unsigned char *nt=hdr+peoff;
    unsigned nsec=*(const uint16_t*)(nt+6);
    unsigned optSize=*(const uint16_t*)(nt+20);
    unsigned secOff=peoff+24+optSize;
    if (nsec==0 || nsec>MAX_SECTIONS || secOff+nsec*40>sizeof(hdr)) { fprintf(log,"bad section table\n"); goto done; }
    secs=(section_t*)calloc(MAX_SECTIONS,sizeof(section_t));
    regs=(region_t*)calloc(MAX_SECTIONS,sizeof(region_t));
    if (!secs||!regs) { fprintf(log,"host allocation failed\n"); goto done; }
    for (unsigned i=0;i<nsec;i++) {
        const unsigned char *sh=hdr+secOff+i*40;
        memcpy(secs[i].name,sh,8); secs[i].name[8]=0;
        secs[i].vsize=le32(sh+8); secs[i].va=le32(sh+12);
        secs[i].raw=le32(sh+16); secs[i].chars=le32(sh+36);
        regs[nregs].rva=secs[i].va; regs[nregs].len=secs[i].vsize;
        regs[nregs].fileOff=0; regs[nregs].exec=(secs[i].chars&0x20000000)?1:0;
        strcpy(regs[nregs].name,secs[i].name);
        nregs++;
    }
    if (!results_init(&r)) { fprintf(log,"host allocation failed\n"); goto done; }
    unsigned maxVsize=0;
    for (unsigned i=0;i<nsec;i++)
        if (secs[i].vsize>maxVsize && secs[i].vsize<=MAX_SECTION_BYTES) maxVsize=secs[i].vsize;
    buf=(unsigned char*)malloc(maxVsize); valid=(unsigned char*)malloc(maxVsize);
    if (!buf||!valid) { fprintf(log,"host allocation failed\n"); goto done; }

    // Coverage is the number that decides how much a NOT FOUND means, so it is
    // measured per pass: total executable bytes, and how many of them this
    // process would actually let us read.
    uint64_t execTotal=0;
    for (unsigned i=0;i<nsec;i++) {
        section_t *s=&secs[i];
        if (s->vsize<16 || s->va==0 || s->vsize>MAX_SECTION_BYTES) continue;
        if (imageBase+s->va+s->vsize>MAX_USER) continue;
        if (s->chars&0x20000000) execTotal+=s->vsize;
    }
    unsigned pageCount=(unsigned)((imageSize+PAGE-1)/PAGE);
    unsigned char *done=(unsigned char*)calloc(pageCount?pageCount:1,1);
    unsigned *firstPass=(unsigned*)calloc(r.npat,sizeof(unsigned));
    if (!firstPass || !done) { fprintf(log,"host allocation failed\n"); free(done); goto done; }

    unsigned pass=0, lastFound=0;
    uint64_t lastScanned=0; unsigned lastExecScanned=0, prevExec=0, lastUnreadable=0;
    double lastPct=0.0, firstPct=0.0;
    uint64_t t0=GetTickCount64();
    for (pass=1; ; pass++) {
        uint64_t scanned=0, execScanned=0; unsigned unreadableTotal=0;
        uint64_t tPass=GetTickCount64();
        if (pass==1) fprintf(log,"\n=== PER-SECTION SCAN ===\n");
        else fprintf(log,"\n=== PASS %u (t+%us) ===\n",pass,(unsigned)((GetTickCount64()-t0)/1000));
        if (pass>1) {
            unsigned np=incremental_pass(process,imageBase,secs,nsec,done,pageCount,minLen,&r);
            fprintf(log,"  re-read %u page%s that were unreadable before (%.1f s)\n",
                np,np==1?"":"s",(double)(GetTickCount64()-tPass)/1000.0);
            scanned=0; unreadableTotal=0;
        }
        for (unsigned i=0;pass==1 && i<nsec;i++) {
            section_t *s=&secs[i];
            if (s->vsize<16 || s->va==0 || s->vsize>MAX_SECTION_BYTES) continue;
            if (imageBase+s->va+s->vsize>MAX_USER) continue;
            unsigned unreadable=0;
            read_with_validity(process,imageBase+s->va,s->vsize,buf,valid,&unreadable);
            scanned+=s->vsize; unreadableTotal+=unreadable;
            if (s->chars&0x20000000) execScanned+=s->vsize-unreadable;
            // Remember which pages this pass could read, so later passes stop
            // re-reading them.
            for (unsigned off=0; off<s->vsize; off+=PAGE) {
                unsigned chunk=(s->vsize-off<PAGE)?(s->vsize-off):PAGE;
                if (!memchr(valid+off,0,chunk)) {
                    unsigned pg=(unsigned)(((uint64_t)s->va+off)/PAGE);
                    if (pg<pageCount) done[pg]=1;
                }
            }
            fprintf(log,"  %-8.8s rva=0x%08lX size=0x%08X unreadable=0x%08X%s\n",
                s->name,(unsigned long)s->va,s->vsize,unreadable,(s->chars&0x20000000)?" EXEC":"");
            if (unreadable && pass==1) protection_notes(process,imageBase+s->va,s->vsize,log);
            if (s->vsize>=65536 && pass==1) {
                unsigned sample=s->vsize<(1u<<20)?s->vsize:(1u<<20);
                if (unreadable==0) {
                    double h=shannon(buf,sample); unsigned pr=0,pd=0;
                    code_signals(buf,sample,&pr,&pd);
                    fprintf(log,"      content: entropy=%.3f prologues=%.0f/MB int3=%.0f/MB\n",
                        h,(double)pr*1048576.0/sample,(double)pd*1048576.0/sample);
                }
            }
            fflush(log);
            results_scan(&r,buf,s->vsize,valid,s->va,minLen);
        }
        results_compact(&r);
        prevExec=lastExecScanned;
        if (pass==1) { lastScanned=scanned; lastUnreadable=unreadableTotal; }
        {
            unsigned long long covered=exec_covered(done,pageCount,secs,nsec);
            if (covered>execTotal) covered=execTotal;
            lastExecScanned=(unsigned)covered;
            lastPct = execTotal ? (100.0*(double)covered/(double)execTotal) : 0.0;
        }
        // Remember the pass on which each pattern first produced a result.
        // g_sigCount, not r.npat: npat is the pattern table PLUS the
        // positive-control slot, and counting the control made every pass claim
        // "5 of 19" while the summary correctly said 4 of 19.
        unsigned found=0;
        for (unsigned p=0;p<r.npat;p++) if (r.counts[p] && !firstPass[p]) firstPass[p]=pass;
        for (unsigned p=0;p<g_sigCount;p++) if (r.counts[p]) found++;
        if (pass==1)
            fprintf(log,"\npass 1: scannedBytes=0x%llX unreadable=0x%X, executable readable %.1f%% of 0x%llX (%.1f s)",
                (unsigned long long)scanned,unreadableTotal,lastPct,(unsigned long long)execTotal,
                (double)(GetTickCount64()-tPass)/1000.0);
        else
            fprintf(log,"\npass %u: executable readable %.1f%% of 0x%llX (%.1f s)",
                pass,lastPct,(unsigned long long)execTotal,(double)(GetTickCount64()-tPass)/1000.0);
        if (pass==1) firstPct=lastPct;
        else fprintf(log," (%.1f%% on pass 1)",firstPct);
        fprintf(log,"\n  patterns matched (control excluded): %u of %u\n",found,g_sigCount);
        if (pass>1 && found==lastFound && execScanned<=(uint64_t)prevExec)
            fprintf(log,"  no new code became readable and no new pattern matched\n");
        lastFound=found;
        fflush(log);
        if (found>=g_sigCount) break;
        if ((GetTickCount64()-t0)/1000 >= watchSeconds) break;
        if (WaitForSingleObject(process,0)!=WAIT_TIMEOUT) {
            fprintf(log,"\nThe client exited during watch mode; reporting what was seen.\n");
            break;
        }
        Sleep(5000);
    }
    fprintf(log,"\nscannedBytes=0x%llX unreadableBytes=0x%X (module virtual size 0x%lX)\n",
        (unsigned long long)lastScanned,lastUnreadable,(unsigned long)imageSize);
    fprintf(log,"executable bytes readable: %u of 0x%llX (%.1f%%), over %u pass%s\n",
        lastExecScanned,(unsigned long long)execTotal,lastPct,pass,pass==1?"":"es");
    uint64_t site=0; int confirmed=control_present(&r,&site);
    report_control(&r,log,confirmed);
    report_results(&r,log,regs,nregs,0,lastPct,firstPass,pass);
    // The two statements are alternatives, and the previous version printed the
    // reassuring one after the honest one at 29% coverage: the second `if` had an
    // `else` that fired whenever coverage had not FALLEN, which includes "never
    // got above 29% in the first place".
    if (lastPct >= 99.0) {
        fprintf(log,"\nAll executable bytes were readable, so a NOT FOUND here is real evidence.\n");
    } else {
        fprintf(log,"\nRemember: process mode can miss code that is not readable. A NOT FOUND here is\n"
                    "not proof of absence. Only %.0f%% of the executable bytes could be read, so the\n"
                    "patterns that are NOT FOUND are UNSEEN, not absent.\n",lastPct);
        if (pass>1 && lastPct < firstPct)
            fprintf(log,"Coverage FELL from %.1f%% to %.1f%% over %u passes: the client re-locks pages as\n"
                        "we read them; it is not decrypting them for us. So the best moment to scan is\n"
                        "right after the client has been used, and --watch can only keep a UNION of the\n"
                        "pages it managed to read (its coverage line cannot go back up when a page is\n"
                        "taken away, because that page was already scanned and its hits are kept).\n",
                firstPct,lastPct,pass);
        else if (pass>1)
            fprintf(log,"Coverage held at %.1f%% across %u passes: nothing new became readable, so\n"
                        "watching longer did not turn any UNSEEN pattern into a real result. Scan once\n"
                        "right after the client has been used instead of waiting.\n",
                lastPct,pass);
    }
    free(firstPass); free(done);
    result=0;
done:
    free(buf); free(valid); free(secs); free(regs); results_free(&r);
    if (process) CloseHandle(process);
    return result;
}

int main(int argc, char **argv) {
    DWORD pid=0; unsigned minLen=16, watch=0; const char *file=NULL; int wantFlat=0;
    for (int i=1;i<argc;i++) {
        if (!strcmp(argv[i],"--pid") && i+1<argc) { pid=(DWORD)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--minlen") && i+1<argc) { minLen=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        if (!strcmp(argv[i],"--file") && i+1<argc) { file=argv[++i]; continue; }
        if (!strcmp(argv[i],"--flat")) { wantFlat=1; continue; }
        if (!strcmp(argv[i],"--watch") && i+1<argc) { watch=(unsigned)strtoul(argv[++i],NULL,10); continue; }
        puts("Usage: WowBSigScan.exe [--file PATH [--flat]] [--pid PID] [--minlen bytes]");
        puts("                        [--watch SECONDS]");
        puts("  --file PATH  scan an image on disk instead of the live process");
        puts("  --flat       for a headerless dump: treat file offset as RVA");
        puts("  --watch SEC  rescan the live process until SEC elapse, accumulating hits as");
        return 2;
    }
    CreateDirectoryW(L"C:\\ghidra-scripts",NULL);
    SYSTEMTIME st; GetLocalTime(&st); wchar_t path[MAX_PATH];
    swprintf(path,MAX_PATH,L"C:\\ghidra-scripts\\WowBSigScan-%04u%02u%02u-%02u%02u%02u.txt",
        st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
    FILE *log=_wfopen(path,L"w");
    if (!log) { puts("Cannot create C:\\ghidra-scripts report."); return 1; }
    fprintf(log,"WOWB SIGNATURE SCANNER v7 (patterns from Winkr-Public sig_defs.h)\n");
    fprintf(log,"Patterns are bytes, not addresses, and were derived on a different build; a\n");
    fprintf(log,"UNIQUE executable match is a strong candidate, not a proof.\n");
    fprintf(log,"v4 adds structure and code-plausibility checks: a match inside a data section is\n");
    fprintf(log,"reported as a lookalike, and a packed/encrypted file is called out instead of\n");
    fprintf(log,"producing results that look meaningful.\n");
    fprintf(log,"v5 reports how much of the executable code it could actually read, and --watch\n");
    fprintf(log,"rescans while you play. A pattern that is NOT FOUND at 30%% coverage has not been\n");
    fprintf(log,"shown absent - it has not been seen.\n");
    fprintf(log,"v6 fixes a double-count that made every repeat hit look like a second site at rva\n");
    fprintf(log,"0 (so unique functions were reported AMBIGUOUS), and states that coverage FALLS\n");
    fprintf(log,"across passes because the client re-locks pages: scan early, not long.\n");
    fprintf(log,"v7 makes the report self-consistent: an UNSEEN pattern is never also called real\n");
    fprintf(log,"evidence, the per-pass count excludes the positive control, and a protected page\n");
    fprintf(log,"is described as unseen at that coverage instead of as a retry that is coming.\n");
    fflush(log);
    int rc = file ? scan_file(file,minLen,wantFlat,log) : scan_process(pid,minLen,watch,log);
    fclose(log);
    char printable[MAX_PATH*3];
    if (WideCharToMultiByte(CP_UTF8,0,path,-1,printable,sizeof(printable),NULL,NULL)) printf("Report: %s\n",printable);
    puts(file ? "Read-only file scan." : "Read-only: no client changes, no restart needed.");
    return rc;
}
