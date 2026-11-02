(function () {
  'use strict';
  // The hand-off page of CONTRACT-PAIR.md section 2. A GET showed nothing secret; the link comes only from a POST this page makes from a click.
  var body = document.body;
  var mode = body.getAttribute('data-mode');            // 'token': opened from a QR code, the token ends the address; 'short': a short code is typed here
  var post = body.getAttribute('data-post');
  var web = body.getAttribute('data-web') || '';
  var openButton = document.getElementById('open');
  var webButton = document.getElementById('web');
  var field = document.getElementById('short');
  var msg = document.getElementById('msg');
  var SCHEME = 'bringyourownagent://connect?';
  var link = null;                                      // in memory only; the second button of a computer uses it
  var busy = false;

  function isMobile() {
    var nav = window.navigator || {};
    if (nav.userAgentData && typeof nav.userAgentData.mobile === 'boolean') return nav.userAgentData.mobile;
    var ua = String(nav.userAgent || '');
    if (/Android|iPhone|iPad|iPod|Mobile|Silk/i.test(ua)) return true;
    return /Macintosh/.test(ua) && Number(nav.maxTouchPoints) > 1;     // an iPad that says it is a Mac
  }
  var mobile = isMobile();

  // <web wallet>#link=<link, as it is>: the fragment never reaches a server (the Android wallet claims this address as an App Link)
  function webAddress(l) {
    if (!web) return null;
    try {
      var u = new URL(web);
      var local = u.protocol === 'http:' && (u.hostname === 'localhost' || u.hostname === '127.0.0.1');
      if (u.protocol !== 'https:' && !local) return null;
      return u.origin + u.pathname + '#link=' + l;
    } catch (e) {
      return null;
    }
  }

  function say(key) { msg.textContent = body.getAttribute('data-' + key) || ''; }

  function lastSegment() {
    var parts = String(window.location.pathname).split('/').filter(function (p) { return p; });
    return parts.length ? parts[parts.length - 1] : '';
  }

  // the link, once: from the server on the first call, from memory after that
  function getLink() {
    if (link) return Promise.resolve(link);
    var payload = mode === 'token' ? { token: lastSegment() } : { short: field.value };
    return fetch(post, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
      cache: 'no-store',
      credentials: 'omit',
      referrerPolicy: 'no-referrer'
    }).then(function (r) {
      return r.json().then(function (j) { return { status: r.status, json: j }; }, function () { return { status: r.status, json: null }; });
    }, function () {
      throw 'network';
    }).then(function (a) {
      if (a.status === 200 && a.json && typeof a.json.link === 'string' && a.json.link.indexOf(SCHEME) === 0) {
        link = a.json.link;
        return link;
      }
      var e = a.json && a.json.error;
      throw (e === 'used' || e === 'gone' || e === 'wrong' || e === 'slow') ? e : (a.status === 429 ? 'slow' : 'network');
    });
  }

  // one click: ask for the link (the first time), then open the wallet. A phone opens the web wallet's address (the app if it claims it);
  // a computer opens the wallet's own link, and its second button the web wallet. The page never tries to learn whether anything started.
  function go(toWeb) {
    if (busy) return;
    busy = true;
    say('working');
    getLink().then(function (l) {
      var target = (toWeb || mobile) ? (webAddress(l) || l) : l;
      say('opened');
      window.location.href = target;
    }, function (e) {
      say(e);
    }).then(function () { busy = false; });
  }

  openButton.addEventListener('click', function () { go(false); });
  webButton.addEventListener('click', function () { go(true); });
  webButton.hidden = mobile || !webAddress('x');         // a phone has one button; a computer gets the second, smaller one

  if (mode === 'short') {
    var changed = function () {
      link = null;                                       // another code, another link
      openButton.disabled = !String(field.value).replace(/[\s-]/g, '');
    };
    field.addEventListener('input', changed);
    field.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' && !openButton.disabled) go(false);
    });
    changed();
  } else {
    openButton.disabled = false;
  }
})();
