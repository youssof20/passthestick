// Small enhancements. The page works without this file: the download button links to the Releases page
// and the join form falls back to the /join page.
(function () {
  "use strict";

  var REPO = "youssof20/passthestick";
  var CODE = /^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{6}$/;

  function parseCode(input) {
    var text = String(input).trim();
    var hash = text.lastIndexOf("#");
    if (hash >= 0) text = text.slice(hash + 1);
    text = text.toUpperCase().replace(/[\s-]/g, "");
    return CODE.test(text) ? text : null;
  }

  // Join: validate here so a typo gets a message on this page instead of a failed connection on the next.
  var form = document.getElementById("join-form");
  if (form) {
    var input = document.getElementById("join-code");
    var error = document.getElementById("join-error");
    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var code = parseCode(input.value);
      if (!code) {
        error.textContent = "That code does not look right. It is six letters and numbers, with no 0, O, 1, I or L.";
        input.focus();
        return;
      }
      error.textContent = "";
      window.location.href = "join/#" + code;
    });
  }

  // Download: point the button at the newest Windows asset on GitHub. If the request fails the Releases link stays.
  var button = document.getElementById("download");
  if (button && "fetch" in window) {
    fetch("https://api.github.com/repos/" + REPO + "/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error("no release")); })
      .then(function (release) {
        var assets = release.assets || [];
        var pick = assets.filter(function (a) { return /\.exe$/i.test(a.name); })[0] || assets.filter(function (a) { return /\.zip$/i.test(a.name); })[0];
        if (!pick) return;
        button.href = pick.browser_download_url;
        var version = String(release.tag_name || "").replace(/^v/, "");
        var note = document.getElementById("download-note");
        if (note && version) note.textContent = "Version " + version + ", " + (pick.size / 1048576).toFixed(0) + " MB. Windows 10 or 11. Free and open source (MIT).";
      })
      .catch(function () { /* keep the fallback link */ });
  }
})();
