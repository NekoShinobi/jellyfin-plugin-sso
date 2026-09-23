"""Check rendered documentation links and fragments under a project subpath."""
import argparse
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__()
        self.links = []
        self.ids = set()
        self.feed(path.read_text())

    def handle_starttag(self, tag, attributes):
        attributes = dict(attributes)
        if "id" in attributes:
            self.ids.add(attributes["id"])
        for key in ("href", "src"):
            if attributes.get(key):
                self.links.append(attributes[key])


def check(root, site_url):
    site_url = site_url.rstrip('/') + '/'
    origin = urlsplit(site_url)
    pages = {path: Page(path) for path in root.rglob('*.html')}
    checked = 0
    errors = []
    for path, page in pages.items():
        relative = path.relative_to(root).as_posix()
        page_url = urljoin(site_url, relative.removesuffix('index.html'))
        for link in page.links:
            target = urlsplit(urljoin(page_url, link))
            if (target.scheme, target.netloc) != (origin.scheme, origin.netloc):
                continue
            if not target.path.startswith(origin.path):
                errors.append(f'{relative}: link escapes project subpath: {link}')
                continue
            destination = root / unquote(target.path[len(origin.path):])
            if destination.is_dir():
                destination /= 'index.html'
            if not destination.is_file():
                errors.append(f'{relative}: missing target: {link}')
            elif target.fragment and destination in pages and unquote(target.fragment) not in pages[destination].ids:
                errors.append(f'{relative}: missing fragment: {link}')
            checked += 1
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Checked {checked} local links and fragments across {len(pages)} rendered pages.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site-dir', type=Path, default=Path('site'))
    parser.add_argument('--site-url', default='https://nekoshinobi.github.io/jellyfin-plugin-sso/')
    args = parser.parse_args()
    check(args.site_dir, args.site_url)
