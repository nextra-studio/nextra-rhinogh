"""Screenshot-inspired, wide installation diagrams for Claude and OpenAI.

Navigation placement follows the user-supplied screenshots. These are explanatory
SVGs, not screenshots; upload/marketplace labels depend on the client version.
"""
from html import escape
from pathlib import Path

STYLE = '''<style>
text{font-family:Segoe UI,Arial,sans-serif;fill:#202124}
.muted{fill:#777a80}.serif{font-family:Georgia,serif}
.paper{fill:#fff;stroke:#dfe1e5}.soft{fill:#f2f3f4}.selected{fill:#eef0f2}
.dark{fill:#17191b}.white{fill:#fff}.line{stroke:#dfe1e5;fill:none}
.focus{fill:none;stroke:#d33b42;stroke-width:2.5}
.number{fill:#d33b42}.blue{fill:#f5f8ff;stroke:#3979ff;stroke-width:1.5}
</style>'''


def text(x, y, value, size=16, cls='', weight=400):
    return f'<text x="{x}" y="{y}" font-size="{size}" class="{cls}" font-weight="{weight}">{escape(value)}</text>'


def rect(x, y, w, h, cls='paper', radius=10):
    return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" class="{cls}"/>'


def count(x, y, n):
    return f'<circle cx="{x}" cy="{y}" r="13" class="number"/>'+text(x-4, y+5, str(n), 14, 'white', 700)


def focus(x, y, w, h, n=None):
    return rect(x, y, w, h, 'focus', 8)+(count(x+w, y, n) if n else '')


def button(x, y, w, value):
    return rect(x, y, w, 38, 'dark', 12)+text(x+16, y+25, value, 16, 'white', 600)


def search(x, y, w, value='Search'):
    return rect(x, y, w, 36)+f'<circle cx="{x+17}" cy="{y+16}" r="6" fill="none" stroke="#85888c"/><path d="M{x+21} {y+21}l5 5" stroke="#85888c"/>'+text(x+36, y+24, value, 14, 'muted')


def plugin_icon(x, y):
    # Interlocking circular marks echo the Plugins glyph in the navigation rail.
    return (f'<g transform="translate({x} {y})" fill="none" stroke="#202124" stroke-width="1.7">'
            '<circle cx="0" cy="0" r="9"/><path d="M-5-6a6 6 0 0 1 8 8M5 6a6 6 0 0 1-8-8"/>'
            '<path d="M-4-1l4-3 4 3v4l-4 2-4-2z"/></g>')


def claude_nav(menu=None, navigation=False):
    """Full-width header: tabs share one baseline, Add stays at far right."""
    body = rect(0, 0, 852, 260)
    # Sidebar is included to show where Customize lives in the app.
    body += rect(1, 1, 165, 258, 'soft', 9)
    body += text(16, 30, '☰', 19)+search(12, 48, 142)
    for y, value in [(111, '+  New'), (144, 'Projects'), (177, 'Artifacts')]:
        body += text(18, y, value, 14, 'muted')
    body += rect(10, 192, 146, 34, 'selected', 7)+text(18, 214, 'Customize', 15)
    body += text(190, 49, 'Customize', 30, 'serif', 600)
    # Exactly one horizontal row: Skills | Connectors | Plugins | Yours | Discover.
    body += text(190, 104, 'Skills', 14, 'muted')+text(249, 104, 'Connectors', 14, 'muted')
    body += rect(335, 79, 75, 36, 'blue', 8)+text(348, 103, 'Plugins', 14, '', 600)
    body += '<path d="M428 83v29" class="line"/>'
    body += text(443, 103, 'Yours', 14, 'muted')+rect(488, 79, 87, 36)+text(499, 103, 'Discover', 14)
    body += search(592, 79, 142)+button(752, 78, 80, '+ Add')
    body += '<path d="M813 94l5 5 5-5" stroke="#fff" fill="none"/>'
    body += rect(190, 140, 385 if menu else 642, 95, 'soft')
    body += text(208, 168, 'Discover plugins', 18, '', 600)+text(208, 197, 'Tools and skills for your workflows', 15, 'muted')
    if navigation:
        body += focus(7, 189, 152, 40, 1)+focus(332, 76, 81, 42, 2)+focus(748, 74, 88, 46, 3)
    if menu:
        body += rect(590, 121, 244, 128)
        for y, value in [(150, 'Add marketplace'), (181, 'Upload plugin'), (212, 'Create a plugin'), (239, 'Create with Claude')]:
            body += text(608, y, value, 15)
        body += focus(598, 129 if menu == 'marketplace' else 160, 226, 29)
    return body


def openai_nav():
    body = rect(0, 0, 852, 280)+rect(1, 1, 43, 278, 'soft', 9)
    body += '<path d="M13 31l9-8 9 8v12H13z" stroke="#84878b" fill="none" stroke-width="1.7"/>'
    for y in (79, 120, 159):
        body += f'<rect x="15" y="{y}" width="14" height="14" rx="4" fill="none" stroke="#84878b" stroke-width="1.5"/>'
    body += rect(7, 191, 30, 36, 'selected', 8)+plugin_icon(22, 209)
    body += text(13, 257, '···', 20, 'muted')
    body += '<path d="M234 0v280" class="line"/>'
    body += text(60, 36, 'Customize', 22, '', 600)
    body += rect(53, 57, 172, 34, 'selected', 8)+plugin_icon(68, 74)+text(86, 80, 'Plugins', 15)
    body += text(86, 116, 'Skills', 15, 'muted')+text(61, 159, 'Installed', 14, 'muted')
    body += rect(61, 177, 18, 18, 'dark', 4)+text(65, 191, 'N', 13, 'white', 700)+text(86, 191, 'Nextra for Rhino', 14)
    body += text(86, 225, 'GitHub', 14, 'muted')+text(86, 256, 'Plugin Creator', 14, 'muted')
    body += text(260, 49, 'Plugins', 28, '', 600)+search(503, 27, 210, 'Search plugins')
    body += text(731, 51, '↻', 22, 'muted')+button(760, 26, 73, 'Add')
    body += '<path d="M808 42l5 5 5-5" stroke="#fff" fill="none"/>'
    body += text(260, 83, 'Connect plugins to let ChatGPT work across your tools', 15, 'muted')
    body += rect(260, 103, 78, 35, 'soft', 18)+text(279, 126, 'Public', 14, '', 600)+text(361, 126, 'Personal', 14, 'muted')
    body += text(260, 170, 'Popular', 19, '', 600)
    for x, y, name, desc in [(270, 208, 'Gmail', 'Read and manage Gmail'), (558, 208, 'Google Drive', 'Docs, Sheets, Slides'), (270, 250, 'GitHub', 'Repositories and pull requests'), (558, 250, 'Notion', 'Docs, notes, and databases')]:
        body += text(x, y, name, 16, '', 600)+text(x, y+19, desc, 12, 'muted')
    # Only the two actions the user needs: the rail icon, then Add.
    return body+focus(5, 189, 34, 40, 1)+focus(756, 22, 81, 46, 2)


def upload(openai=False):
    filename = 'nextra-rhinogh-gpt-plugin.zip' if openai else 'nextra-rhinogh-plugin.zip'
    body = rect(0, 0, 852, 245)+text(28, 40, 'Upload plugin archive' if openai else 'Upload a plugin', 25, '', 600)
    body += text(28, 68, 'Choose the downloaded ZIP for '+('OpenAI' if openai else 'Claude')+'. Keep it zipped.', 15, 'muted')
    body += text(28, 100, 'Plugin file', 15, '', 600)+rect(28, 112, 796, 68)
    body += text(48, 140, 'Browse or drag and drop your plugin ZIP', 16)+text(48, 166, filename, 15, 'muted')
    body += button(28, 193, 98, 'Upload')+focus(24, 108, 804, 76, 1)+focus(24, 189, 106, 46, 2)
    return body


def marketplace_choice():
    return (rect(0, 0, 852, 240)+text(28, 43, 'Add marketplace', 25, '', 600)
            +rect(28, 67, 796, 64)+text(50, 94, 'Browse Anthropic sources', 17, '', 600)
            +text(50, 117, 'Curated marketplaces of plugins from Anthropic and partners', 14, 'muted')
            +rect(28, 147, 796, 64)+text(50, 174, 'Add from a repository', 17, '', 600)
            +text(50, 197, 'Sync a plugin marketplace from a GitHub repository or Git URL', 14, 'muted')
            +focus(24, 143, 804, 72))


def repository(openai=False):
    value = 'https://github.com/Tatsuki0123/nextra-rhinogh' if openai else 'Tatsuki0123/nextra-rhinogh'
    return (rect(0, 0, 852, 230)+text(28, 43, 'Add a marketplace' if openai else 'Add marketplace', 25, '', 600)
            +text(28, 83, 'Repository URL' if openai else 'URL', 15, '', 600)
            +rect(28, 95, 796, 44, 'blue')+text(45, 123, value, 17)
            +text(28, 164, 'Use the repository root, without a branch or folder URL.', 14, 'muted')
            +button(708, 180, 116, 'Add / sync' if openai else 'Sync')
            +focus(24, 91, 804, 52, 1)+focus(704, 176, 124, 46, 2))


def install(openai=False):
    return (rect(0, 0, 852, 180)+text(28, 43, 'Nextra marketplace', 25, '', 600)
            +rect(28, 65, 796, 90)+rect(48, 87, 44, 44, 'dark')+text(60, 117, 'N', 26, 'white', 700)
            +text(111, 101, 'Nextra for Rhino' if openai else 'nextra-rhinogh', 19, '', 600)
            +text(111, 128, 'Rhino + Grasshopper tools and skill', 15, 'muted')
            +button(705, 90, 96, 'Install')+focus(701, 86, 104, 46))


def uploader_menu():
    return (rect(0, 0, 852, 160)+text(28, 41, 'Plugins → Add', 24, '', 600)
            +text(28, 73, 'Open Create plugin, then choose the archive uploader.', 16, 'muted')
            +button(28, 99, 151, 'Create plugin')+button(236, 99, 237, 'Upload plugin archive')
            +text(194, 125, '→', 24)+focus(232, 95, 245, 46))


def marketplace_menu():
    return (rect(0, 0, 852, 160)+text(28, 41, 'Plugins → Add → Create plugin', 24, '', 600)
            +text(28, 73, 'Choose marketplace setup where available.', 16, 'muted')
            +button(28, 99, 196, 'Add a marketplace')+focus(24, 95, 204, 46)
            +text(258, 113, 'Managed workspace:', 15, '', 600)
            +text(258, 137, 'Admin → Plugins → Add → Import marketplace', 15, 'muted'))


def write(out, name, title, steps):
    y=60
    body=text(24, 33, title, 22, '', 600)
    for n, (heading, drawing, h) in enumerate(steps, 1):
        body += count(37, y+12, n)+text(61, y+18, heading, 18, '', 600)
        body += f'<g transform="translate(24 {y+36})">{drawing}</g>'
        y += h+66
    body += text(24, y+5, 'Simplified guide based on the app UI · labels may vary by version.', 13, 'muted')
    height=y+24
    Path(out, name).write_text(f'<svg xmlns="http://www.w3.org/2000/svg" width="900" height="{height}" viewBox="0 0 900 {height}">{STYLE}<rect width="900" height="{height}" rx="14" fill="#f7f8fa"/>{body}</svg>\n', encoding='utf-8', newline='\n')


def generate(out):
    write(out, 'claude-plugins.svg', 'Claude · Find Plugins', [('Customize → Plugins → Add', claude_nav(navigation=True), 260)])
    write(out, 'openai-plugins.svg', 'OpenAI · Find Plugins', [('Plugins icon → Add', openai_nav(), 280)])
    write(out, 'claude-upload.svg', 'Claude · A — Install from ZIP (recommended)', [
        ('Add → Upload plugin', claude_nav('upload'), 260), ('Choose the Claude ZIP → Upload', upload(), 245)])
    write(out, 'openai-upload.svg', 'OpenAI · A — Install from ZIP (recommended)', [
        ('Open the archive uploader', uploader_menu(), 160), ('Choose the GPT ZIP → Upload', upload(True), 245)])
    write(out, 'claude-marketplace.svg', 'Claude · B — Install from marketplace', [
        ('Add → Add marketplace', claude_nav('marketplace'), 260), ('Choose Add from a repository', marketplace_choice(), 240),
        ('Enter the repository → Sync', repository(), 230), ('Select Nextra → Install', install(), 180)])
    write(out, 'openai-marketplace.svg', 'OpenAI · B — Install from marketplace', [
        ('Open marketplace setup', marketplace_menu(), 160), ('Enter the repository → Add / sync', repository(True), 230),
        ('Select Nextra → Install', install(True), 180)])
