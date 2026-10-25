#!/bin/bash
# Double-click this in Finder to install Async on a Mac. It runs install.sh from this folder in a
# Terminal window, so there's nothing to type.
cd "$(dirname "$0")" || exit 1
# Files downloaded from the internet are quarantined; these come with Async and aren't signed by Apple.
xattr -dr com.apple.quarantine . 2>/dev/null
./install.sh
echo
read -n 1 -s -r -p "All done. Press any key to close this window."
