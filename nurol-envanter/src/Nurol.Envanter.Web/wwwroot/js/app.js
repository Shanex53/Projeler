// Tarayıcıya özel küçük yardımcılar. Kalıcı veri burada tutulmaz.
window.ne = {
  get: function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
  set: function (k, v) { try { localStorage.setItem(k, v); } catch (e) { } },
  focus: function (id) { var el = document.getElementById(id); if (el) el.focus(); },

  // Kamera ile QR / barkod okutma. Tarayıcının yerleşik BarcodeDetector'ını kullanır (Android Chrome, Edge vb.).
  // Desteklenmeyen tarayıcılarda el tipi okuyucu (klavye gibi çalışır) veya elle giriş kullanılır.
  scan: {
    stream: null, timer: null,
    supported: function () { return 'BarcodeDetector' in window && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia); },
    start: async function (videoId, dotnet) {
      var wanted = ['qr_code', 'code_128', 'code_39', 'ean_13', 'data_matrix'];
      var formats = wanted;
      try { var sup = await BarcodeDetector.getSupportedFormats(); formats = wanted.filter(function (f) { return sup.indexOf(f) >= 0; }); } catch (e) { }
      var detector = new BarcodeDetector({ formats: formats });
      var video = document.getElementById(videoId);
      try {
        this.stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
      } catch (e) {
        return e && e.name === 'NotAllowedError' ? 'izin' : 'kamera';
      }
      video.srcObject = this.stream;
      await video.play();
      var self = this, busy = false;
      this.timer = setInterval(async function () {
        if (busy) return;
        busy = true;
        try {
          var codes = await detector.detect(video);
          if (codes.length > 0) { self.stop(); await dotnet.invokeMethodAsync('OnCode', codes[0].rawValue); }
        } catch (e) { }
        busy = false;
      }, 300);
      return 'ok';
    },
    stop: function () {
      if (this.timer) { clearInterval(this.timer); this.timer = null; }
      if (this.stream) { this.stream.getTracks().forEach(function (t) { t.stop(); }); this.stream = null; }
    }
  }
};
