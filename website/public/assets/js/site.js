/* ==================================================================
   Casium — landing page behaviour
   header state · mobile nav · reveal · code tabs · faq · copy · spy
   ================================================================== */
(() => {
  "use strict";

  const $  = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ---------------------------------------------------- sticky header */
  const topbar = $("#topbar");
  if (topbar) {
    const onScroll = () => topbar.dataset.stuck = String(window.scrollY > 8);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
  }

  /* ---------------------------------------------------- mobile nav */
  const toggle = $("#navtoggle");
  const mobile = $("#mobilenav");
  if (toggle && mobile) {
    const setOpen = (open) => {
      toggle.setAttribute("aria-expanded", String(open));
      mobile.dataset.open = String(open);
    };
    toggle.addEventListener("click", () => setOpen(toggle.getAttribute("aria-expanded") !== "true"));
    $$("a", mobile).forEach((a) => a.addEventListener("click", () => setOpen(false)));
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && mobile.dataset.open === "true") { setOpen(false); toggle.focus(); }
    });
  }

  /* ---------------------------------------------------- reveal on scroll */
  const revealables = $$(".reveal");
  if (reduceMotion || !("IntersectionObserver" in window)) {
    revealables.forEach((el) => el.classList.add("is-in"));
  } else {
    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add("is-in");
            io.unobserve(entry.target);
          }
        });
      },
      { rootMargin: "0px 0px -8% 0px", threshold: 0.08 }
    );
    revealables.forEach((el) => io.observe(el));
  }

  /* ---------------------------------------------------- code tabs */
  const tablist = $('[role="tablist"][aria-label="Code samples"]');
  if (tablist) {
    const tabs = $$('[role="tab"]', tablist);
    const select = (tab, focus = false) => {
      tabs.forEach((t) => {
        const on = t === tab;
        t.setAttribute("aria-selected", String(on));
        t.tabIndex = on ? 0 : -1;
        const pane = document.getElementById(t.getAttribute("aria-controls"));
        if (pane) pane.hidden = !on;
      });
      if (focus) tab.focus();
    };
    tabs.forEach((tab, i) => {
      tab.addEventListener("click", () => select(tab));
      tab.addEventListener("keydown", (e) => {
        const map = { ArrowRight: 1, ArrowLeft: -1, Home: -i, End: tabs.length - 1 - i };
        if (e.key in map) {
          e.preventDefault();
          const next = tabs[(i + map[e.key] + tabs.length) % tabs.length];
          select(next, true);
        }
      });
    });

    const copyBtn = $("#copycode");
    if (copyBtn) {
      copyBtn.addEventListener("click", async () => {
        const pane = tabs.map((t) => document.getElementById(t.getAttribute("aria-controls")))
                         .find((p) => p && !p.hidden);
        const text = pane ? pane.innerText.trim() : "";
        try {
          await navigator.clipboard.writeText(text);
        } catch {
          const ta = document.createElement("textarea");
          ta.value = text;
          ta.style.position = "fixed";
          ta.style.opacity = "0";
          document.body.appendChild(ta);
          ta.select();
          try { document.execCommand("copy"); } catch { /* clipboard unavailable */ }
          ta.remove();
        }
        const original = copyBtn.innerHTML;
        copyBtn.innerHTML = '<svg viewBox="0 0 16 16" fill="none"><path d="m3 8.4 3.2 3.2L13 4.8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
        copyBtn.style.color = "var(--mint)";
        setTimeout(() => { copyBtn.innerHTML = original; copyBtn.style.color = ""; }, 1400);
      });
    }
  }

  /* ---------------------------------------------------- faq accordion */
  const faq = $("#faqlist");
  if (faq) {
    const items = $$(".faq-item", faq);
    const setOpen = (item, open) => {
      item.dataset.open = String(open);
      const btn = $(".faq-q", item);
      const body = $(".faq-a", item);
      btn.setAttribute("aria-expanded", String(open));
      body.style.height = open ? `${body.firstElementChild.offsetHeight}px` : "0px";
    };
    items.forEach((item) => {
      const btn = $(".faq-q", item);
      setOpen(item, false);
      btn.addEventListener("click", () => {
        const willOpen = item.dataset.open !== "true";
        items.forEach((other) => { if (other !== item) setOpen(other, false); });
        setOpen(item, willOpen);
      });
    });
    window.addEventListener("resize", () => {
      items.forEach((item) => {
        if (item.dataset.open === "true") {
          const body = $(".faq-a", item);
          body.style.height = `${body.firstElementChild.offsetHeight}px`;
        }
      });
    });
  }

  /* ---------------------------------------------------- scroll spy */
  const navLinks = $$('.nav a[href^="#"]');
  if (navLinks.length && "IntersectionObserver" in window) {
    const sections = navLinks
      .map((a) => document.getElementById(a.getAttribute("href").slice(1)))
      .filter(Boolean);
    const spy = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          navLinks.forEach((a) =>
            a.setAttribute("aria-current", String(a.getAttribute("href") === `#${entry.target.id}`))
          );
        });
      },
      { rootMargin: "-45% 0px -50% 0px" }
    );
    sections.forEach((s) => spy.observe(s));
  }
})();
