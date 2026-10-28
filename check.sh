#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
python3 -c "import playwright" 2>/dev/null || python3 -m pip install playwright
python3 -m unittest discover -s tests -p "*_test.py"
