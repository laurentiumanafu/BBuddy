#!/usr/bin/env python3
"""Compare stopped 70938 bag-research snapshots, without asserting item identity.

Usage: python Tools/analyze_mail_bag_snapshots.py <bot-log>
Each observation is read-only and non-atomic. Logs may contain sensitive data.
"""
import argparse
import re
from dataclasses import dataclass, field
from pathlib import Path

HEADER=re.compile(r'70938 read-only inventory research snapshot;')
PLAYER=re.compile(r'player GUID (0x[0-9A-Fa-f]+)')
CANDIDATE=re.compile(r'container-candidate\[(\d+)\] identity=(0x[0-9A-Fa-f]+) object=0x[0-9A-Fa-f]+ raw\+0xAC0=(\d+)')
RAW=re.compile(r'raw\+0x488 candidate 16-byte entries=([0-9A-Fa-f]*)')
UNRESOLVED=re.compile(r'container-candidate\[(\d+)\]')

@dataclass
class Snapshot:
    player: str = ''
    bags: dict = field(default_factory=dict)


def parse(text):
    result=[]; current=None; pending=None
    for line in text.splitlines():
        if HEADER.search(line):
            current=Snapshot(); result.append(current); pending=None; continue
        if current is None: continue
        match=PLAYER.search(line)
        if match: current.player=match.group(1).upper(); continue
        match=CANDIDATE.search(line)
        if match:
            i=int(match.group(1)); pending=(i,match.group(2).upper(),int(match.group(3)))
            current.bags.pop(i,None); continue
        match=UNRESOLVED.search(line)
        if match:
            current.bags.pop(int(match.group(1)),None); pending=None; continue
        match=RAW.search(line)
        if match and pending:
            i,guid,size=pending; value=match.group(1).upper(); pending=None
            if 0<=i<=4 and size<=64 and len(value)==32*size:
                current.bags[i]=(guid,tuple(value[j:j+32] for j in range(0,len(value),32)))
    return result


def compare(a,b):
    if not a.player or a.player!=b.player: return ['Player identity unavailable or changed; snapshots not comparable.']
    lines=[]
    for index in range(5):
        first=a.bags.get(index); second=b.bags.get(index)
        if not first or not second or first[0]!=second[0] or len(first[1])!=len(second[1]):
            lines.append(f'candidate {index}: unknown or changed identity/size'); continue
        changes=[i for i,(old,new) in enumerate(zip(first[1],second[1])) if old!=new]
        lines.append(f'candidate {index}: changed entry indexes {changes}; no item/slot/mail meaning established')
    return lines


def main():
    ap=argparse.ArgumentParser(description=__doc__); ap.add_argument('log',type=Path); args=ap.parse_args()
    data=parse(args.log.read_text(encoding='utf-8-sig',errors='replace'))
    print(f'{len(data)} 70938 snapshot(s) found. No bag or mail semantics are proven.')
    for i in range(1,len(data)):
        print(f'\nSnapshot {i} -> {i+1}:')
        for line in compare(data[i-1],data[i]): print('  '+line)
    if len(data)<2: print('Need at least two stopped snapshots from the same player and session to compare.')
if __name__=='__main__': main()
