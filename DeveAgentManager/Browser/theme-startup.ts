(function () {
    var mode = "system";
    try { mode = localStorage.getItem("dam-theme") || "system"; } catch (e) { }
    var dark = mode === "dark" || (mode !== "light" && !(window.matchMedia && matchMedia("(prefers-color-scheme: light)").matches));
    document.documentElement.dataset.theme = dark ? "dark" : "light";
    var meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
    if (meta) meta.content = dark ? "#0d1017" : "#f5f6fa";
})();
