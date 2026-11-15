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
UNRESOLVED=re.compile(r'container-candidate\[(\d+)\] (?:raw identity zero|identity=.*pool matches=|no unique stable|\+0xAC0 unreadable|raw \+0xAC0=.*exceeds|unstable or unreadable)')
PLAYER_HEADER=re.compile(r'candidate count=(\d+); array=(0x[0-9A-Fa-f]+); stride=16; previous=(.*)')
PLAYER_ENTRY=re.compile(r'entry\[(\d+)\] ([0-9A-Fa-f]{32})(?: -> ([0-9A-Fa-f]{32}))?')

@dataclass
class Snapshot:
    player: str = ''
    bags: dict = field(default_factory=dict)
    player_entries: dict = field(default_factory=dict)
    player_array: str = ''
    player_count: int = 0


def parse(text):
    result=[]; current=None; pending=None; player_section=False
    for line in text.splitlines():
        if HEADER.search(line):
            current=Snapshot(); result.append(current); pending=None; player_section=False; continue
        if current is None: continue
        match=PLAYER.search(line)
        if match: current.player=match.group(1).upper(); continue
        match=PLAYER_HEADER.search(line)
        if match:
            current.player_count=int(match.group(1)); current.player_array=match.group(2).upper()
            player_section=current.player_count<=256
            if player_section and len(result)>1 and match.group(3).startswith('comparable'):
                old=result[-2]
                if old.player==current.player and old.player_count==current.player_count and old.player_array==current.player_array:
                    current.player_entries=old.player_entries.copy()
            continue
        if 'special-index research:' in line or 'container research:' in line: player_section=False
        match=PLAYER_ENTRY.search(line) if player_section else None
        if match:
            index=int(match.group(1)); old=match.group(2).upper(); new=match.group(3)
            if index >= current.player_count: current.player_entries.clear(); player_section=False; continue
            if new is None: current.player_entries[index]=old
            elif current.player_entries.get(index)==old: current.player_entries[index]=new.upper()
            else: current.player_entries.clear(); player_section=False
            continue
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
    if (a.player_array and a.player_array==b.player_array and a.player_count==b.player_count and
        len(a.player_entries)==a.player_count and len(b.player_entries)==b.player_count):
        changes=[i for i in range(a.player_count) if a.player_entries[i]!=b.player_entries[i]]
        lines.append(f'player array: changed entry indexes {changes}; special range and backpack mapping remain observational')
    else: lines.append('player array: unknown, incomplete, or changed identity/size')
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
