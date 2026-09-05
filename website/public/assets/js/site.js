/* ==================================================================
   Casium — landing page behaviour (deliberately tiny)
   ================================================================== */
(() => {
  "use strict";

  const topbar = document.getElementById("topbar");
  if (!topbar) return;

  const onScroll = () => {
    topbar.dataset.stuck = String(window.scrollY > 8);
  };
  onScroll();
  window.addEventListener("scroll", onScroll, { passive: true });
})();
