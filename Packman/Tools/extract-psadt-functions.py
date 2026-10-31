#!/usr/bin/env python3
"""Rebuilds Packman/MonacoEditor/psadt-functions.json from two PSAppDeployToolkit checkouts.

    python3 Packman/Tools/extract-psadt-functions.py \
        --release DIR --release-version 4.1.8 --dev DIR --dev-version 4.2 \
        --out Packman/MonacoEditor/psadt-functions.json

DIR is a checkout of github.com/PSAppDeployToolkit/PSAppDeployToolkit (the release tag and main).
The comment-based help of every Public/*.ps1 function becomes one catalog entry. A function found
in only one checkout gets in_release / in_dev accordingly. The editor reads these two flags and the
release / dev version strings, so a new PSADT release needs this script and no code change.
"""
import argparse
import json
import pathlib
import re

SECTION = re.compile(r'^\s*\.(\w+)\s*(.*)$')
KEYWORDS = {'SYNOPSIS', 'DESCRIPTION', 'PARAMETER', 'EXAMPLE', 'INPUTS', 'OUTPUTS', 'NOTES', 'LINK'}
SOURCE = 'github.com/PSAppDeployToolkit/PSAppDeployToolkit src/PSAppDeployToolkit/Public'
DOCS_URL = 'https://psappdeploytoolkit.com/docs/reference/functions/'


def parse(path):
    src = path.read_text(encoding='utf-8-sig')
    m = re.search(r'function\s+([\w-]+)\s*\{\s*<#(.*?)#>', src, re.S)
    if not m:
        return None
    name, help_text = m.group(1), m.group(2)

    sections, current, lines = [], None, []
    for line in help_text.splitlines():
        h = SECTION.match(line)
        if h and h.group(1).upper() in KEYWORDS:
            if current:
                sections.append((current[0], current[1], '\n'.join(lines).strip()))
            current, lines = (h.group(1).upper(), h.group(2).strip()), []
        else:
            lines.append(line.strip())
    if current:
        sections.append((current[0], current[1], '\n'.join(lines).strip()))

    def first(keyword):
        return next((text for kind, _, text in sections if kind == keyword), '')

    # Types, mandatory flags and parameter sets come from the param() block, not the help.
    declared = {}
    block = re.search(r'param\s*\((.*?)\n\s*\)\s*\n', src[m.end():], re.S | re.I)
    if block:
        for chunk in re.split(r',\s*\n(?=\s*\[)', block.group(1)):
            hits = re.findall(r'\[([\w\.\[\],]+)\]\s*\$(\w+)', chunk)
            if hits:
                type_name, param_name = hits[-1]
                declared[param_name] = {
                    'type': type_name,
                    'mandatory': bool(re.search(r'Mandatory\s*=\s*\$true|Mandatory\s*[,\)]', chunk)),
                    'sets': sorted(set(re.findall(r"ParameterSetName\s*=\s*'([^']+)'", chunk))),
                }

    parameters = []
    for kind, arg, text in sections:
        if kind == 'PARAMETER':
            d = declared.get(arg, {})
            parameters.append({
                'name': arg,
                'type': d.get('type', ''),
                'mandatory': d.get('mandatory', False),
                'parameter_sets': d.get('sets', []),
                'description': ' '.join(text.split()),
            })

    return {
        'name': name,
        'verb': name.split('-')[0],
        'synopsis': ' '.join(first('SYNOPSIS').split()),
        'description': first('DESCRIPTION'),
        'parameters': parameters,
        'examples': [text.strip() for kind, _, text in sections if kind == 'EXAMPLE'],
        'outputs': ' '.join(first('OUTPUTS').split()),
        'docs_url': DOCS_URL + name,
    }


def load(root):
    root = pathlib.Path(root)
    files = sorted(list(root.glob('src/PSAppDeployToolkit/Public/*.ps1'))
                   + list(root.glob('modules/PSAppDeployToolkit/Public/*.ps1')))
    functions = (parse(p) for p in files)
    return {f['name']: f for f in functions if f}


def build(release_dir, release_version, dev_dir, dev_version):
    release, dev = load(release_dir), load(dev_dir)
    merged = {}
    for name, fn in release.items():
        fn['in_release'], fn['in_dev'] = True, name in dev
        merged[name] = fn
    for name, fn in dev.items():
        if name not in merged:
            fn['in_release'], fn['in_dev'] = False, True
            merged[name] = fn
    functions = sorted(merged.values(), key=lambda f: f['name'])
    return {'source': SOURCE, 'release': release_version, 'dev': dev_version,
            'count': len(functions), 'functions': functions}


def write(catalog, out):
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(catalog, f, indent=2)
        f.write('\n')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--release', required=True, help='checkout of the release tag')
    ap.add_argument('--release-version', required=True, help='for example 4.1.8')
    ap.add_argument('--dev', required=True, help='checkout of the development branch')
    ap.add_argument('--dev-version', required=True, help='for example 4.2')
    ap.add_argument('--out', required=True, help='catalog file to write')
    args = ap.parse_args()

    catalog = build(args.release, args.release_version, args.dev, args.dev_version)
    write(catalog, args.out)
    functions = catalog['functions']
    print(f"{catalog['count']} functions; "
          f"{sum(f['in_release'] for f in functions)} in {args.release_version}; "
          f"{sum(not f['in_release'] for f in functions)} new in {args.dev_version}; "
          f"{sum(not f['in_dev'] for f in functions)} gone in {args.dev_version}")


if __name__ == '__main__':
    main()
