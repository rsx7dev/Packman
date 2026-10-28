"""Concept 1 'Quiet Ledger': hideable left nav / bottom menubar, at 1440 and 1280, light and dark."""
import os, pathlib, shutil, unittest
from playwright.sync_api import sync_playwright

PAGE = (pathlib.Path(__file__).resolve().parent.parent / "out/design/concept-1/index.html").as_uri()
VIEWS = [(w, t) for w in (1440, 1280) for t in ("light", "dark")]
SCREENS = ["create", "upload", "remote-test", "applications", "advanced", "settings"]
FOOTED = [s for s in SCREENS if s != "advanced"]  # Advanced has no page footer
CHROMIUM = os.environ.get("CHROMIUM") or shutil.which("chromium") or shutil.which("chromium-browser") or shutil.which("google-chrome")


def luminance(rgb):
    c = [v / 255 for v in rgb]
    c = [v / 12.92 if v <= .03928 else ((v + .055) / 1.055) ** 2.4 for v in c]
    return .2126 * c[0] + .7152 * c[1] + .0722 * c[2]


def contrast(a, b):
    hi, lo = sorted([luminance(a), luminance(b)], reverse=True)
    return (hi + .05) / (lo + .05)


class Concept1Nav(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pw = sync_playwright().start()
        cls.browser = cls.pw.chromium.launch(executable_path=CHROMIUM)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.pw.stop()

    def go(self, page, screen):
        page.evaluate(f"location.hash = '#{screen}'")
        page.wait_for_selector(f"[data-screen={screen}].active")

    def open(self, width, theme="light"):
        page = self.browser.new_page(viewport={"width": width, "height": 800})
        self.addCleanup(page.close)
        page.goto(PAGE + "#applications")
        page.evaluate(f"document.documentElement.dataset.theme = '{theme}'")
        return page

    def state(self, page):
        return page.evaluate("""() => {
          const shown = s => getComputedStyle(document.querySelector(s)).display !== 'none';
          return { hidden: document.getElementById('app').classList.contains('nav-hidden'),
                   nav: shown('.nav'), bar: shown('.menubar'), navW: document.querySelector('.nav').getBoundingClientRect().width };
        }""")

    def assert_visible_nav(self, page):
        s = self.state(page)
        self.assertEqual((s["hidden"], s["nav"], s["bar"], s["navW"]), (False, True, False, 208))

    def assert_hidden_nav(self, page):
        s = self.state(page)
        self.assertEqual((s["hidden"], s["nav"], s["bar"]), (True, False, True))

    def test_default_has_left_nav_and_no_menubar(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                self.assert_visible_nav(page)

    def test_titlebar_toggle_swaps_nav_and_menubar(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                page.click("#nav-toggle")
                self.assert_hidden_nav(page)
                page.click("#nav-toggle")
                self.assert_visible_nav(page)

    def test_ctrl_b_toggles_both_directions(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                page.keyboard.press("Control+b")
                self.assert_hidden_nav(page)
                page.keyboard.press("Control+b")
                self.assert_visible_nav(page)

    def test_reload_restores_default(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                page.click("#nav-toggle")
                page.reload()
                self.assert_visible_nav(page)

    def test_menubar_is_a_row_that_leaves_page_footers_clear(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                page.click("#nav-toggle")
                for screen in FOOTED:
                    self.go(page, screen)
                    gap = page.evaluate("""() => {
                      const bar = document.querySelector('.menubar').getBoundingClientRect();
                      const pf = document.querySelector('[data-screen].active .pf').getBoundingClientRect();
                      return { gap: bar.top - pf.bottom, pfH: pf.height, barBottom: bar.bottom, vh: innerHeight };
                    }""")
                    self.assertGreaterEqual(gap["gap"], 0, screen)
                    self.assertGreater(gap["pfH"], 0, screen)
                    self.assertLessEqual(gap["barBottom"], gap["vh"], screen)

    def test_aria_current_follows_route_in_both_navs(self):
        page = self.open(1440)
        for screen in SCREENS:
            self.go(page, screen)
            current = page.evaluate("""() => ['.nav', '.menubar'].map(n =>
              [...document.querySelectorAll(n + ' a[aria-current=page]')].map(a => a.dataset.nav))""")
            self.assertEqual(current, [[screen], [screen]], screen)

    def test_edit_script_keeps_the_nav_it_was_opened_from(self):
        page = self.open(1440)
        for origin in ("create", "applications"):
            self.go(page, origin)
            page.evaluate(f"document.querySelector('[data-from={origin}]').click()")
            page.wait_for_selector("[data-screen=edit-script].active")
            current = page.evaluate("""() => ['.nav', '.menubar'].map(n =>
              [...document.querySelectorAll(n + ' a[aria-current=page]')].map(a => a.dataset.nav))""")
            self.assertEqual(current, [[origin], [origin]], origin)

    def test_menubar_reads_on_both_themes(self):
        for width, theme in VIEWS:
            with self.subTest(width=width, theme=theme):
                page = self.open(width, theme)
                page.click("#nav-toggle")
                page.wait_for_timeout(400)
                c = page.evaluate("""() => {
                  const rgb = e => getComputedStyle(e).color.match(/\\d+/g).slice(0, 3).map(Number);
                  const bg = e => getComputedStyle(e).backgroundColor.match(/\\d+/g).slice(0, 3).map(Number);
                  const bar = document.querySelector('.menubar');
                  const idle = bar.querySelector('a:not([aria-current])');
                  const active = bar.querySelector('a[aria-current]');
                  return { text: rgb(idle), bar: bg(bar), page: bg(document.querySelector('.page')), activeText: rgb(active), activeBg: bg(active) };
                }""")
                self.assertGreaterEqual(contrast(c["text"], c["bar"]), 4.5)
                self.assertGreaterEqual(contrast(c["activeText"], c["activeBg"]), 4.5)
                self.assertGreaterEqual(contrast(c["bar"], c["page"]), 1.1)


if __name__ == "__main__":
    unittest.main()
