#!/usr/bin/env python3
"""Brightness Dimmer launcher.

Thin wrapper around the package entry point so the CLI argument definitions live
in a single place (autobright/__main__.py).
"""

from autobright.__main__ import main

if __name__ == '__main__':
    main()
