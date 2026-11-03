// Sends first-time visitors of the root page to their language, then highlights their platform's download.
(function () {
  var root = document.documentElement;
  var path = location.pathname;
  var isRoot = path === '/' || /\/index\.html$/.test(path) && !/\/(en|ja)\//.test(path);
  var saved = null;
  try { saved = localStorage.getItem('lang'); } catch (e) {}
  if (isRoot && !saved) {
    var pref = (navigator.languages && navigator.languages[0]) || navigator.language || '';
    if (/^ja/i.test(pref)) { location.replace('ja/'); return; }
    if (!/^zh/i.test(pref)) { location.replace('en/'); return; }
  }
  var fab = document.querySelector('.lang-fab');
  if (fab) {
    var ball = fab.querySelector('.lang-ball');
    var setOpen = function (open) {
      fab.classList.toggle('open', open);
      ball.setAttribute('aria-expanded', String(open));
    };
    ball.addEventListener('click', function () {
      var open = !fab.classList.contains('open');
      setOpen(open);
      if (open) fab.querySelector('.lang-options a').focus({ preventScroll: true });
    });
    document.addEventListener('click', function (e) { if (!fab.contains(e.target)) setOpen(false); });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && fab.classList.contains('open')) { setOpen(false); ball.focus(); }
    });
  }

  document.querySelectorAll('a[hreflang]').forEach(function (a) {
    a.addEventListener('click', function () {
      try { localStorage.setItem('lang', a.getAttribute('hreflang')); } catch (e) {}
    });
  });

  var ua = navigator.userAgent;
  var platform = /Mac/.test(ua) && !/iPhone|iPad/.test(ua) ? 'mac' : /Windows/.test(ua) ? 'win' : null;
  if (!platform) return;
  var card = document.getElementById('dl-' + platform);
  var first = card.parentNode.firstElementChild;
  card.classList.add('recommended');
  if (card !== first) card.parentNode.insertBefore(card, first);
  var hero = document.getElementById('hero-download');
  hero.href = card.querySelector('.pill').href;
  hero.textContent = hero.getAttribute('data-' + platform);
})();
