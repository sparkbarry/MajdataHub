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
  document.querySelectorAll('.langs a').forEach(function (a) {
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
