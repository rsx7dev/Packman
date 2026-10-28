#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
python3 -c "import playwright" 2>/dev/null || python3 -m pip install playwright
# the tests use a system Chromium (or $CHROMIUM); without one, fetch Playwright's own
if [ -z "${CHROMIUM:-}" ] && ! command -v chromium chromium-browser google-chrome >/dev/null 2>&1; then
  python3 -m playwright install chromium
fi
python3 -m unittest discover -s tests -p "*_test.py"
