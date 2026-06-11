(function () {
  'use strict';

  const DAVOS_URL = 'http://localhost:27834/';
  const STALE_MS  = 5000; // Don't re-send more often than this
  let lastSentAt  = 0;
  let lastUrl     = location.href;

  // ---------- DOM TEXT EXTRACTION ----------
  function extractText() {
    const SKIP_TAGS = new Set(['script', 'style', 'noscript', 'head', 'svg', 'canvas']);
    const parts = [];
    let totalLen = 0;
    const MAX_LEN = 8000;

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        const el = node.parentElement;
        if (!el) return NodeFilter.FILTER_REJECT;
        if (SKIP_TAGS.has(el.tagName.toLowerCase())) return NodeFilter.FILTER_REJECT;
        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden') return NodeFilter.FILTER_REJECT;
        const t = node.textContent.trim();
        if (t.length === 0) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      }
    });

    let node;
    while ((node = walker.nextNode()) && totalLen < MAX_LEN) {
      const t = node.textContent.trim();
      parts.push(t);
      totalLen += t.length + 1;
    }
    return parts.join(' ').replace(/\s+/g, ' ').trim();
  }

  // ---------- IMAGE EXTRACTION (canvas Base64) ----------
  function extractImages() {
    const imgs = Array.from(document.querySelectorAll('img'));
    const canvas = document.createElement('canvas');
    const ctx2d  = canvas.getContext('2d');
    const results = [];

    for (const img of imgs) {
      if (results.length >= 6) break;
      if (!img.complete || img.naturalWidth < 80 || img.naturalHeight < 80) continue;

      try {
        const MAX = 320;
        const scale = Math.min(MAX / img.naturalWidth, MAX / img.naturalHeight, 1);
        canvas.width  = Math.round(img.naturalWidth  * scale);
        canvas.height = Math.round(img.naturalHeight * scale);
        ctx2d.clearRect(0, 0, canvas.width, canvas.height);
        ctx2d.drawImage(img, 0, 0, canvas.width, canvas.height);
        const b64 = canvas.toDataURL('image/jpeg', 0.65).split(',')[1];
        if (b64 && b64.length > 100) results.push(b64);
      } catch (_) {
        // CORS-blocked — skip silently
      }
    }
    return results;
  }

  // ---------- SEND ----------
  function send() {
    const now = Date.now();
    if (now - lastSentAt < STALE_MS) return;
    lastSentAt = now;

    const payload = {
      url:    location.href,
      title:  document.title,
      text:   extractText(),
      images: extractImages()
    };

    fetch(DAVOS_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).catch(() => {}); // Silent fail — Davos may not be running
  }

  // ---------- TRIGGERS ----------
  // Initial send
  if (document.readyState === 'complete') send();
  else window.addEventListener('load', send, { once: true });

  // SPA navigation: watch for URL changes via MutationObserver
  new MutationObserver(() => {
    if (location.href !== lastUrl) {
      lastUrl = location.href;
      setTimeout(send, 800); // Wait for SPA content to render
    }
  }).observe(document.documentElement, { subtree: true, childList: true });

  // Re-send on user focus (switching back to this tab)
  window.addEventListener('focus', send);
})();
