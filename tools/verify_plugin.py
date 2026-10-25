"""Check generated marketplace/plugin paths, archive contents, and MCP startup.

Run after tools/make_plugin.py. This does not install a plugin or edit Rhino.
"""
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import zipfile

REPO = Path(__file__).resolve().parent.parent
DIST = REPO / "dist"
NAME = "nextra-rhinogh"
ROOT = REPO / "plugin" / NAME


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def check_openai(root):
    manifest = read_json(root / ".codex-plugin/plugin.json")
    require(manifest["name"] == NAME, "OpenAI identity differs")
    require(len(manifest["interface"]["shortDescription"]) <= 30, "OpenAI subtitle too long")
    for path in [manifest["skills"], manifest["mcpServers"],
                 manifest["interface"]["logo"], manifest["interface"]["composerIcon"]]:
        require(path.startswith("./"), f"Plugin resource must start with ./: {path}")
        resolved = (root / path).resolve()
        require(resolved.is_relative_to(root.resolve()) and resolved.exists(), f"Invalid resource: {path}")
    config = read_json(root / manifest["mcpServers"])["mcpServers"][NAME]
    require(config["command"] == "node" and config["cwd"] == ".", "Unexpected OpenAI launch configuration")
    require(config["args"] == ["server/index.js"], "Unexpected bridge entry point")
    require((root / "skills" / NAME / "SKILL.md").is_file(), "Missing packaged skill")
    return manifest, config


def check_mcp(root, config):
    messages = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "nextra-package-check", "version": "1"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
    ]
    result = subprocess.run([config["command"], *config["args"]], cwd=root / config["cwd"],
        env={**os.environ, **config.get("env", {})},
        input="".join(json.dumps(m) + "\n" for m in messages),
        capture_output=True, text=True, encoding="utf-8", timeout=15, check=True)
    replies = {m["id"]: m for line in result.stdout.splitlines()
               if (m := json.loads(line)).get("id") in (1, 2)}
    require("result" in replies.get(1, {}), "MCP initialize failed")
    tools = replies.get(2, {}).get("result", {}).get("tools", [])
    require({t["name"] for t in tools} == {"view", "query", "run", "gh_edit", "undo", "memory"},
            "MCP did not expose all six tools")


def main():
    claude = read_json(REPO / ".claude-plugin/marketplace.json")["plugins"][0]
    openai = read_json(REPO / ".agents/plugins/marketplace.json")["plugins"][0]
    require(claude["source"] == openai["source"]["path"] == f"./plugin/{NAME}", "Catalog paths differ")
    require((ROOT / ".claude-plugin/plugin.json").exists(), "Missing Claude plugin manifest")
    require(read_json(ROOT / ".mcp.json")["mcpServers"][NAME]["args"] == ["${CLAUDE_PLUGIN_ROOT}/server/index.js"],
            "Claude bridge is not relative to installed plugin root")
    manifest, config = check_openai(ROOT)
    require(manifest["version"] == claude["version"] == read_json(REPO / "bridge/manifest.json")["version"],
            "Package versions differ")
    for source, dest in [(REPO / "bridge/server", ROOT / "server"),
                         (REPO / "skill/nextra", ROOT / "skills" / NAME)]:
        for path in source.rglob("*"):
            if path.is_file():
                require(path.read_bytes() == (dest / path.relative_to(source)).read_bytes(),
                        f"Generated file differs from source: {path}")
    check_mcp(ROOT, config)
    for filename in [f"{NAME}-plugin.zip", f"{NAME}-gpt-plugin.zip", f"{NAME}-agent-kit.zip"]:
        with zipfile.ZipFile(DIST / filename) as archive, tempfile.TemporaryDirectory(dir=DIST) as temp:
            require(archive.testzip() is None, f"Damaged archive: {filename}")
            for entry in archive.namelist():
                path = PurePosixPath(entry)
                require(not path.is_absolute() and ".." not in path.parts and "\\" not in entry,
                        f"Unsafe archive path: {entry}")
            archive.extractall(temp)
            extracted = Path(temp)
            if filename.endswith("agent-kit.zip"):
                kit = extracted / f"{NAME}-kit"
                require(read_json(kit / ".claude-plugin/marketplace.json")["plugins"][0] == claude,
                        "Kit marketplace differs")
                require((kit / ".codex/config.toml").is_file() and (kit / "AGENTS.md").is_file(),
                        "Kit workspace configuration missing")
                plugin_root = kit / "plugin" / NAME
            else:
                plugin_root = extracted
                require(read_json(extracted / ".claude-plugin/plugin.json") ==
                        read_json(ROOT / ".claude-plugin/plugin.json"), "ZIP Claude manifest differs")
            _, launch = check_openai(plugin_root)
            check_mcp(plugin_root, launch)
        print(f"PASS {filename}: paths, manifests, archive integrity, MCP initialize + six tools")
    print("PASS repository catalogs, source synchronization, versions, icons, and local MCP startup")


if __name__ == "__main__":
    main()
