// Tarayıcıya özel küçük yardımcılar. Kalıcı veri burada tutulmaz; yalnızca görünüm tercihleri.
window.ne = {
  get: function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
  set: function (k, v) { try { localStorage.setItem(k, v); } catch (e) { } },
  focus: function (id) { var el = document.getElementById(id); if (el) el.focus(); }
};
