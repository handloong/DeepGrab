#!/usr/bin/env python3

import re
import sys
import json
import subprocess
import requests

from lxml import html

PATTERN_URL = r'https://[\w.-]*pornhub\.\w+/view_video\.php\?viewkey=[\da-z]+'
PATTERN_SAFE_FNAME = r'[^A-Za-z0-9._\-]'

HEADERS = {
    'User-Agent': (
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) '
        'AppleWebKit/537.36 (KHTML, like Gecko) '
        'Chrome/124.0.0.0 Safari/537.36'
    ),
    'Referer': 'https://www.pornhub.com/'
}


def fetch_page(url):
    resp = requests.get(url, headers=HEADERS)
    resp.raise_for_status()
    return html.fromstring(resp.content)


def extract_video_show(dom):
    for script in dom.xpath('//script'):
        text = script.text_content()
        m = re.search(r'var VIDEO_SHOW\s*=\s*(\{.*?\});', text, re.DOTALL)
        if not m:
            continue
        try:
            return json.loads(m.group(1))
        except Exception:
            pass
    return None


def get_title(dom, video_show):
    title = (
        video_show.get('videoTitleOriginal')
        or video_show.get('videoTitle')
    )
    if title:
        return title
    h1 = dom.xpath('//h1')
    if h1:
        return h1[0].text_content().strip()
    return 'video'


def extract_links(dom, video_show):
    links = []

    media_defs = video_show.get('mediaDefinitions', [])

    for item in media_defs:
        if not isinstance(item, dict):
            continue

        video_url = item.get('videoUrl')

        if video_url and video_url.endswith('.json'):
            try:
                r = requests.get(video_url, headers=HEADERS)
                r.raise_for_status()
                data = r.json()
                if isinstance(data, list):
                    for x in data:
                        if not isinstance(x, dict):
                            continue
                        u = x.get('videoUrl')
                        if u:
                            links.append(u)
            except Exception as e:
                print(f'JSON parse failed: {e}')

        elif video_url and video_url.startswith('http'):
            links.append(video_url)

    # fallback scan
    page_text = html.tostring(dom, encoding='unicode')
    found = re.findall(r'https:[^"\']+', page_text)
    for url in found:
        url = url.replace('\\/', '/')
        if '.mp4' in url or '.m3u8' in url:
            links.append(url)

    return list(dict.fromkeys(links))


def resolve_master_m3u8(master_url):
    """Fetch master.m3u8 and return the URL of the highest-bandwidth stream."""
    try:
        r = requests.get(master_url, headers=HEADERS)
        r.raise_for_status()
        lines = r.text.splitlines()

        streams = []
        i = 0
        while i < len(lines):
            line = lines[i]
            if line.startswith('#EXT-X-STREAM-INF'):
                bw = 0
                m = re.search(r'BANDWIDTH=(\d+)', line)
                if m:
                    bw = int(m.group(1))
                if i + 1 < len(lines):
                    uri = lines[i + 1].strip()
                    if uri and not uri.startswith('#'):
                        if not uri.startswith('http'):
                            base = master_url.rsplit('/', 1)[0]
                            uri = base + '/' + uri
                        streams.append((bw, uri))
                i += 2
            else:
                i += 1

        if streams:
            streams.sort(key=lambda x: x[0], reverse=True)
            print(f'\nResolved streams from master.m3u8:')
            for bw, uri in streams:
                print(f'  [{bw}] {uri}')
            return streams[0][1]

    except Exception as e:
        print(f'Failed to resolve master.m3u8: {e}')

    return None


def choose_best_link(links):
    priorities = ['1080', '720', '480', '360']

    hls_masters = [x for x in links if 'master.m3u8' in x]

    # Try preferred quality first
    for q in priorities:
        for link in hls_masters:
            if q in link:
                resolved = resolve_master_m3u8(link)
                if resolved:
                    return resolved

    # Fallback: any master.m3u8
    for link in hls_masters:
        resolved = resolve_master_m3u8(link)
        if resolved:
            return resolved

    # MP4 fallback
    mp4_links = [x for x in links if '.mp4' in x and '.m3u8' not in x]
    for q in priorities:
        for link in mp4_links:
            if q in link:
                return link

    if mp4_links:
        return mp4_links[0]

    return None


def sanitize_filename(name):
    return re.sub(PATTERN_SAFE_FNAME, '', name.lower().replace(' ', '_'))


def download_ffmpeg(url, filename):
    print(f'\nDownloading:\n{url}')
    print(f'\nSaving to:\n{filename}')

    cmd = [
        'ffmpeg', '-y',
        '-headers',
        (
            f'User-Agent: {HEADERS["User-Agent"]}\r\n'
            f'Referer: {HEADERS["Referer"]}\r\n'
        ),
        '-i', url,
        '-movflags', '+faststart',
        '-c', 'copy',
        filename
    ]

    subprocess.run(cmd, check=True)


def main():
    if len(sys.argv) < 2:
        print('Usage:')
        print('python ph.py <url>')
        sys.exit(1)

    url = sys.argv[1]

    if not re.match(PATTERN_URL, url):
        print('Bad Pornhub URL')
        sys.exit(1)

    print(f'Fetching page:\n{url}')

    dom = fetch_page(url)
    video_show = extract_video_show(dom)

    if not video_show:
        print('Could not extract VIDEO_SHOW')
        sys.exit(1)

    title = get_title(dom, video_show)
    print(f'\nTitle:\n{title}')

    links = extract_links(dom, video_show)

    if not links:
        print('No video links found.')
        sys.exit(1)

    print('\nFound links:\n')
    for i, link in enumerate(links, 1):
        print(f'[{i}] {link}')

    dl_link = choose_best_link(links)

    if not dl_link:
        print('\nNo usable links found.')
        sys.exit(1)

    print(f'\nSelected:\n{dl_link}')

    filename = sanitize_filename(title) + '.mp4'

    try:
        download_ffmpeg(dl_link, filename)
    except FileNotFoundError:
        print('\nERROR: ffmpeg not found in PATH')
        sys.exit(1)

    print('\nDone!')


if __name__ == '__main__':
    main()