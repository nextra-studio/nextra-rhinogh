#!/bin/bash
# NextraRhinoGh setup for Mac (the Windows version is NextraRhinoGh-Setup.bat). It installs Node.js, which the AI apps
# use to start the Rhino bridge. Re-running is safe: if Node.js 18 or newer is already installed, nothing is changed.
#
# Easiest way to run it: open Terminal and paste
#   curl -fsSL https://raw.githubusercontent.com/nextra-studio/nextra-rhinogh/main/NextraRhinoGh-Setup.command | bash
# or, after downloading this file:  bash ~/Downloads/NextraRhinoGh-Setup.command
# (Double-clicking a downloaded .command file is often blocked by macOS: no "execute" permission, unidentified developer.)
#
# Test switch: NEXTRA_SETUP_DRYRUN=1 finds the installer URL but installs nothing.

NODE_PAGE="https://nodejs.org/en/download"
GUIDE="https://github.com/nextra-studio/nextra-rhinogh#install"
NODE_OK=0
NODE_VER=""

pause() {
  # only when a person is at the keyboard (not when piped from curl without a terminal)
  if [ -t 1 ] && [ -r /dev/tty ]; then printf '\n Press Return to close.'; read -r _ </dev/tty; fi
}

# Sets NODE_OK=1 and NODE_VER when Node.js 18 or newer is found: on PATH, or in the folders the official installer and
# Homebrew use (a fresh install is not on this window's PATH yet, and apps started from the Dock may not see Homebrew's).
check_node() {
  NODE_OK=0; NODE_VER=""
  for n in "$(command -v node 2>/dev/null)" /usr/local/bin/node /opt/homebrew/bin/node; do
    [ -n "$n" ] && [ -x "$n" ] || continue
    v="$("$n" --version 2>/dev/null)" || continue
    major="${v#v}"; major="${major%%.*}"
    case "$major" in ''|*[!0-9]*) continue ;; esac
    if [ "$major" -ge 18 ]; then NODE_OK=1; NODE_VER="$v"; NODE_EXE="$n"; return 0; fi
    echo "       Found Node.js $v at $n, but 18 or newer is needed."
  done
  return 1
}

echo
echo " NextraRhinoGh setup (Mac)"
echo " ========================="
echo
echo " [1/2] Looking for Node.js ..."
if check_node; then
  echo "       Node.js $NODE_VER is already installed ($NODE_EXE). Nothing to do."
else
  echo
  echo " [2/2] Downloading the Node.js LTS installer from nodejs.org ..."
  # index.tab columns: version date files npm v8 uv zlib openssl modules lts security; newest first, lts = "-" or a name
  ver="$(curl -fsSL https://nodejs.org/dist/index.tab 2>/dev/null | awk -F'\t' 'NR > 1 && $10 != "-" { print $1; exit }')"
  if [ -z "$ver" ]; then
    failed=1
  else
    url="https://nodejs.org/dist/$ver/node-$ver.pkg"     # one universal installer for Intel and Apple Silicon
    echo "       $url"
    if [ "${NEXTRA_SETUP_DRYRUN:-0}" = "1" ]; then
      echo "       (dry run: not downloading or installing)"
      pause; exit 0
    fi
    pkg="${TMPDIR:-/tmp}/nextra-node-lts.pkg"
    if curl -fL --progress-bar "$url" -o "$pkg"; then
      echo "       Installing (macOS asks for your Mac password; nothing shows while you type) ..."
      if sudo installer -pkg "$pkg" -target / </dev/tty; then
        rm -f "$pkg"
        check_node && echo && echo " Node.js $NODE_VER is installed." && echo " Quit and reopen your AI app (ChatGPT or Claude) so it finds Node.js."
      else
        echo "       The installer did not finish; opening it so you can click through it instead."
        open "$pkg"
        echo "       When it is done, quit and reopen your AI app."
      fi
    else
      failed=1
    fi
  fi
  if [ "${failed:-0}" = "1" ]; then
    echo
    echo " Node.js could not be installed automatically."
    echo " Download the LTS macOS Installer (.pkg) from:"
    echo
    echo "     $NODE_PAGE"
    echo
    echo " Run it with the default options, then quit and reopen your AI app."
    open "$NODE_PAGE" 2>/dev/null
    pause; exit 1
  fi
fi

echo
echo " Next: install the Nextra-RhinoGh plug-in in Rhino and the NextraRhinoGh plug-in in your AI app."
echo " Guide: $GUIDE"
pause
exit 0
