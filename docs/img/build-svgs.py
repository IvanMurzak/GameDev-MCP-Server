"""Regenerate the README images in docs/img/ with the shared svgkit style: py -3 docs/img/build-svgs.py

The terminal shows real output of gamedev-mcp-server 9.5.2 (port shown as 8080; the configure run used a
throwaway ~/my-game folder, so its pin and port are just an example).
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import svgkit as K  # noqa: E402


def hero_right():
    cx, cy = 960, 190
    s = K.wire("M884 190 L902 190", color=K.RED_TEXT, dur=1.6)
    for i, y in enumerate((110, 190, 270)):
        s += K.wire(f"M1018 190 L1072 {y}", color=K.BLUE, dur=2.2, begin=i * 0.6)
    s += K.mark(cx, cy, 50)
    s += K.pill(770, cy - 17, "AI agent", color=K.TEXT)[0]
    for y, name in ((110, "Unity"), (190, "Godot"), (270, "Unreal")):
        s += K.pill(1072, y - 17, name, color=K.TEXT, w=100)[0]
    return s


K.write(os.path.join(HERE, "hero.svg"), K.hero(
    title="GameDev ", accent="MCP Server", eyebrow_text="AI Game Developer",
    subtitle=["One MCP server for the Unity, Godot", "and Unreal editor plugins."],
    chip_items=[".NET 9", "stdio", "HTTP", "Docker"], right=hero_right(),
    label="GameDev MCP Server: one MCP server between AI agents and the Unity, Godot and Unreal editor plugins"))

body = (K.eyebrow(190, 64, "MCP client", "middle") + K.eyebrow(600, 64, "This repository", "middle")
        + K.eyebrow(1015, 64, "Engine plugin", "middle")
        + K.wire("M340 230 L450 230", color=K.RED_TEXT, dur=1.8)
        + K.wire("M750 230 C810 230 810 135 870 135", color=K.GREEN, dur=2.2)
        + K.wire("M750 230 L870 230", color=K.BLUE, dur=1.9, begin=0.5)
        + K.wire("M750 230 C810 230 810 325 870 325", color=K.YELLOW, dur=2.4, begin=1.0)
        + f'<text x="395" y="218" text-anchor="middle" class="m" font-size="16" fill="{K.SECOND}">MCP</text>'
        + f'<rect x="770" y="240" width="80" height="26" rx="13" fill="{K.SURF}" stroke="{K.HAIR_STRONG}"/><text x="810" y="258" text-anchor="middle" class="m" font-size="16" fill="{K.SECOND}">SignalR</text>'
        + K.card(40, 130, 300, 200, "AI agent", "Claude Code · Codex · Cursor", accent="blue", sub2="Copilot · Gemini · …")
        + K.card(450, 120, 300, 220, "gamedev-mcp-server", "MCP over stdio or HTTP", accent="red",
                 mono_title=True, sub2="auth: none · token · oauth")
        + K.card(870, 95, 290, 80, "Unity-MCP", "Unity Editor and games", accent="green")
        + K.card(870, 190, 290, 80, "Godot-MCP", "Godot Editor and games", accent="blue")
        + K.card(870, 285, 290, 80, "Unreal-MCP", "Unreal Editor and games", accent="yellow")
        + f'<text x="600" y="420" text-anchor="middle" class="s" font-size="18" fill="{K.SECOND}">'
          'Tools, prompts and resources come from the plugin that connects.</text>')
K.write(os.path.join(HERE, "architecture.svg"), K.panel(1200, 470, body,
        "An AI agent talks MCP over stdio or HTTP to gamedev-mcp-server, which forwards each call over SignalR "
        "to the Unity-MCP, Godot-MCP or Unreal-MCP plugin"))

K.write(os.path.join(HERE, "terminal.svg"), K.terminal(title="~/my-game — gamedev-mcp-server 9.5.2", lines=[
    {"cmd": "gamedev-mcp-server --client-transport streamableHttp --port 8080"},
    {"out": [("muted", "Parsed arguments: { "), ("key", '"Port"'), ("muted", ": "), ("num", "8080"), ("muted", ", "),
             ("key", '"ClientTransport"'), ("muted", ": "), ("str", '"streamableHttp"'), ("muted", ", … }")]},
    {"out": [("ok", "info: "), ("text", "Start listening on port: 8080 (bind: loopback)")]},
    {"out": [("ok", "info: "), ("text", "MCP auth mode: none (transport: streamableHttp, bind: loopback).")], "note": "plugin connects"},
    {"cmd": "gamedev-mcp-server configure --agent claude-code"},
    {"out": [("ok", "Configured Claude Code (claude-code).")]},
    {"out": [("muted", "  transport : "), ("text", "streamableHttp")]},
    {"out": [("muted", "  pin       : "), ("num", "728ad6d4")], "note": "from the project path"},
    {"out": [("muted", "  config    : "), ("link", "~/my-game/.mcp.json")], "note": "URL only"},
], hold=14.0, label="A terminal session: gamedev-mcp-server starts on port 8080 with the streamable HTTP transport, "
         "then configure writes a pinned Claude Code config for the project"))
