#!/usr/bin/env python3
"""Read the deployed application shell and verify its CSP-compatible screen CSS.

This deliberately checks the served HTML and response headers, not a local build.
It does not authenticate, execute JavaScript or mutate application data.
"""
import argparse
import base64
import datetime
import hashlib
from html.parser import HTMLParser
import json
import pathlib
import urllib.parse
import urllib.request


class AssetCheckError(RuntimeError):
    """The served production shell violates the checked asset contract."""


class _Shell(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stylesheets = []
        self.scripts = []
        self.handlers = []
        self.meta_policies = []
        self.base_href = None
        self.noscript = 0
        self.script = None

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == "noscript":
            self.noscript += 1
        if self.noscript:
            return
        self.handlers.extend(value for name, value in attrs if name.startswith("on") and value and value.strip())
        if tag == "base" and self.base_href is None:
            self.base_href = values.get("href")
        if tag == "meta" and values.get("http-equiv", "").lower() == "content-security-policy":
            self.meta_policies.append(values.get("content", ""))
        if tag == "link" and "stylesheet" in values.get("rel", "").lower().split():
            if "disabled" not in values and "alternate" not in values.get("rel", "").lower().split():
                self.stylesheets.append(values)
        if tag == "script":
            self.script = {"attrs": values, "text": ""}
            self.scripts.append(self.script)

    def handle_endtag(self, tag):
        if tag == "noscript":
            self.noscript = max(0, self.noscript - 1)
        if tag == "script":
            self.script = None

    def handle_data(self, data):
        if self.script is not None and not self.noscript:
            self.script["text"] += data


def _policy(value):
    directives = {}
    for directive in value.split(";"):
        fields = directive.split()
        if fields:
            directives.setdefault(fields[0].lower(), fields[1:])
    return directives


def _sources(policy, directive, fallback):
    return policy.get(directive, policy.get(fallback, policy.get("default-src")))


def _allows_inline(sources, body, nonce=None, handler=False):
    if sources is None:
        return True
    signed = any(value.startswith(("'nonce-", "'sha256-", "'sha384-", "'sha512-")) for value in sources)
    if "'unsafe-inline'" in sources and not signed:
        return True
    if not handler and nonce and "'nonce-" + nonce + "'" in sources:
        return True
    if not handler or "'unsafe-hashes'" in sources:
        for algorithm in ("sha256", "sha384", "sha512"):
            digest = base64.b64encode(hashlib.new(algorithm, body.encode("utf-8")).digest()).decode("ascii")
            if "'" + algorithm + "-" + digest + "'" in sources:
                return True
    return False


def _origin(url):
    value = urllib.parse.urlsplit(url)
    return value.scheme.lower(), value.hostname, value.port or (443 if value.scheme.lower() == "https" else 80)


def validate_home_page(html, csp_headers, page_url):
    """Validate a fetched shell; return its normal same-origin screen stylesheet URLs.

    Nonces/hashes can authorize an inline script, but unrestricted inline execution
    is rejected. Noscript fallbacks do not satisfy the screen-stylesheet check.
    """
    if not csp_headers or not all(value.strip() for value in csp_headers):
        raise AssetCheckError("The served home page has no enforced Content-Security-Policy header.")
    shell = _Shell()
    shell.feed(html)
    policies = [_policy(value) for value in [*csp_headers, *shell.meta_policies]]
    for policy in policies:
        for directive in ("script-src-elem", "script-src-attr"):
            if _allows_inline(_sources(policy, directive, "script-src"), "untrusted inline probe", handler=directive.endswith("-attr")):
                raise AssetCheckError("The production CSP must restrict inline scripts and event handlers.")
    for handler in shell.handlers:
        if not all(_allows_inline(_sources(policy, "script-src-attr", "script-src"), handler, handler=True) for policy in policies):
            raise AssetCheckError("The served home page contains an inline event handler prohibited by its CSP.")
    executable_types = {"", "module", "importmap", "speculationrules", "text/javascript", "application/javascript", "text/ecmascript", "application/ecmascript"}
    for script in shell.scripts:
        attrs = script["attrs"]
        if not attrs.get("src") and attrs.get("type", "").lower().split(";")[0].strip() in executable_types and script["text"].strip():
            if not all(_allows_inline(_sources(policy, "script-src-elem", "script-src"), script["text"], attrs.get("nonce")) for policy in policies):
                raise AssetCheckError("The served home page contains an executable inline script prohibited by its CSP.")
    base_url = urllib.parse.urljoin(page_url, shell.base_href or "")
    stylesheets = []
    for link in shell.stylesheets:
        if link.get("media", "").strip().lower() not in ("", "all", "screen"):
            continue
        href = link.get("href")
        if not href:
            raise AssetCheckError("A screen stylesheet has no URL.")
        url = urllib.parse.urljoin(base_url, href)
        if _origin(url) != _origin(page_url):
            raise AssetCheckError("Production screen stylesheets must be served from the application origin.")
        for policy in policies:
            sources = _sources(policy, "style-src-elem", "style-src")
            if sources is not None and "'self'" not in sources and "*" not in sources:
                raise AssetCheckError("The served CSP does not permit the application's screen stylesheet.")
        if url not in stylesheets:
            stylesheets.append(url)
    if not stylesheets:
        raise AssetCheckError("The served home page has no normal screen/all stylesheet outside noscript; print/onload-only CSS cannot style this application.")
    if len(stylesheets) > 32:
        raise AssetCheckError("The served home page exceeds the 32-stylesheet verification budget.")
    return stylesheets


def check_production_assets(base_url, timeout=15):
    """Read-only HTTP check, callable from container smoke and directly from a CLI."""
    target = urllib.parse.urlsplit(base_url)
    if target.scheme not in ("http", "https") or not target.hostname or target.username or target.password or target.path not in ("", "/") or target.query or target.fragment:
        raise AssetCheckError("Supply an explicit HTTP(S) application origin without credentials, path, query or fragment.")
    page_url = urllib.parse.urlunsplit((target.scheme, target.netloc, "/", "", ""))

    def fetch(url, accept, limit):
        request = urllib.request.Request(url, headers={"Accept": accept, "Cache-Control": "no-cache"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            if _origin(response.url) != _origin(page_url):
                raise AssetCheckError("An application asset redirected to a different origin.")
            data = response.read(limit + 1)
            if len(data) > limit:
                raise AssetCheckError("An application asset exceeds the verification byte budget.")
            return response.url, response.headers, data

    actual_url, headers, html = fetch(page_url, "text/html", 2_097_152)
    if headers.get_content_type() != "text/html":
        raise AssetCheckError("The served home page is not HTML.")
    csp = headers.get_all("Content-Security-Policy", [])
    urls = validate_home_page(html.decode(headers.get_content_charset() or "utf-8"), csp, actual_url)
    assets = []
    for url in urls:
        actual_css, css_headers, css = fetch(url, "text/css", 8_388_608)
        if css_headers.get_content_type() != "text/css" or not css.strip():
            raise AssetCheckError("A screen stylesheet is unavailable, empty or returned a non-CSS response.")
        assets.append({"url": actual_css, "bytes": len(css), "sha256": hashlib.sha256(css).hexdigest()})
    return {"status": "passed", "completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "homePage": actual_url, "htmlSha256": hashlib.sha256(html).hexdigest(), "contentSecurityPolicy": csp,
            "stylesheets": assets, "checks": ["enforced CSP restricts inline scripts and event handlers", "no CSP-blocked inline handlers or executable scripts", "normal screen/all stylesheet outside noscript", "same-origin nonempty text/css response"],
            "boundary": "Read-only served-asset check; does not execute the application or certify all CSP/browser behavior."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("origin", help="Explicit deployed application origin, such as http://127.0.0.1:8080")
    parser.add_argument("--output", type=pathlib.Path, help="Write the JSON result to this file as well as stdout")
    args = parser.parse_args()
    try:
        report = check_production_assets(args.origin)
    except (AssetCheckError, OSError, UnicodeError, ValueError) as error:
        parser.exit(1, f"Production asset check failed: {error}\n")
    payload = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload)
    print(payload, end="")


if __name__ == "__main__":
    main()
