namespace PhotoTri.App.Places;

/// <summary>
/// Page de la carte (Leaflet + fonds OpenStreetMap), affichée dans un WebView2.
/// La page reçoit les lieux par setPlaces() et renvoie la clé du lieu cliqué par postMessage().
/// </summary>
static class MapPage
{
    public const string Html = """
        <!doctype html>
        <html lang="fr">
        <head>
        <meta charset="utf-8">
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <style>
          html, body, #map { margin: 0; height: 100%; font-family: "Segoe UI", sans-serif; }
          #offline { display: none; position: absolute; inset: 0; align-items: center; justify-content: center;
                     color: #666; background: #f3f3f3; text-align: center; padding: 20px; }
        </style>
        </head>
        <body>
        <div id="map"></div>
        <div id="offline">La carte a besoin d'une connexion Internet.<br>Les lieux restent disponibles dans la liste.</div>
        <script>
        let map = null, layer = null, markers = {};
        if (typeof L === "undefined") {
          document.getElementById("offline").style.display = "flex";
        } else {
          map = L.map("map", { worldCopyJump: true }).setView([46.5, 6.5], 6);
          L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
            maxZoom: 19,
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">les contributeurs d’OpenStreetMap</a>'
          }).addTo(map);
          layer = L.layerGroup().addTo(map);
        }

        function setPlaces(places) {
          if (!map) return;
          layer.clearLayers();
          markers = {};
          for (const p of places) {
            const m = L.circleMarker([p.lat, p.lon], {
              radius: Math.min(28, 6 + Math.sqrt(p.count) * 2.2),
              color: "#1565c0", fillColor: "#42a5f5", fillOpacity: 0.65, weight: 2
            }).bindTooltip(p.label + " (" + p.count + ")");
            m.on("click", () => window.chrome.webview.postMessage(p.key));
            m.addTo(layer);
            markers[p.key] = { m, p };
          }
        }

        // Centre la carte sur ces lieux (tous si la liste est vide) et les met en rouge.
        function focusPlaces(keys) {
          if (!map) return;
          const all = Object.entries(markers);
          for (const [k, x] of all) {
            const on = keys.includes(k);
            x.m.setStyle({ color: on ? "#c62828" : "#1565c0", fillColor: on ? "#ef5350" : "#42a5f5" });
            if (on) x.m.bringToFront();
          }
          const pts = (keys.length ? all.filter(([k]) => keys.includes(k)) : all).map(([, x]) => [x.p.lat, x.p.lon]);
          if (pts.length === 1) map.setView(pts[0], 12);
          else if (pts.length > 1) map.fitBounds(pts, { padding: [30, 30], maxZoom: 12 });
        }
        </script>
        </body>
        </html>
        """;
}
