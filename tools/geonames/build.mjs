// Construit src/PhotoTri.Core/Data/cities.tsv.gz à partir des fichiers GeoNames (CC BY 4.0) :
//   https://download.geonames.org/export/dump/cities500.zip (décompressé : villes de plus de 500 habitants)
//   https://download.geonames.org/export/dump/admin1CodesASCII.txt
//   https://download.geonames.org/export/dump/alternateNamesV2.zip (décompressé, ~750 Mo, pour les noms français)
// Usage : node build.mjs <dossier contenant ces fichiers>
// Format de sortie, une ville par ligne : lat \t lon \t ville \t région \t code pays \t pays \t population
import { createReadStream, readFileSync, writeFileSync } from "node:fs";
import { createInterface } from "node:readline";
import { gzipSync } from "node:zlib";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const source = process.argv[2];
const out = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "src", "PhotoTri.Core", "Data", "cities.tsv.gz");

const regions = new Map(); // "FR.84" → { id, name }
for (const line of readFileSync(join(source, "admin1CodesASCII.txt"), "utf8").split("\n")) {
  const [code, name, , id] = line.split("\t");
  if (code && name) regions.set(code, { id: id?.trim(), name });
}

// Quartiers (PPLX), lieux historiques, abandonnés ou détruits : pas des villes où l'on prend des photos.
const skipped = new Set(["PPLX", "PPLH", "PPLQ", "PPLW", "PPLCH"]);
const cities = [];
for (const line of readFileSync(join(source, "cities500.txt"), "utf8").split("\n")) {
  const c = line.split("\t");
  if (c.length < 15 || skipped.has(c[7])) continue;
  cities.push({ id: c[0], lat: Number(c[4]).toFixed(4), lon: Number(c[5]).toFixed(4), name: c[1], region: regions.get(`${c[8]}.${c[10]}`), cc: c[8], pop: c[14] });
}

// Noms français : le nom « préféré » s'il existe, sinon le premier nom français courant.
const wanted = new Set([...cities.map(c => c.id), ...[...regions.values()].map(r => r.id)]);
const french = new Map(); // id → { name, preferred }
const lines = createInterface({ input: createReadStream(join(source, "alternateNamesV2.txt"), "utf8"), crlfDelay: Infinity });
for await (const line of lines) {
  const c = line.split("\t");
  if (c[2] !== "fr" || !wanted.has(c[1]) || c[6] === "1" || c[7] === "1") continue; // familier ou historique
  const preferred = c[4] === "1";
  const current = french.get(c[1]);
  if (!current || (preferred && !current.preferred)) french.set(c[1], { name: c[3], preferred });
}

const countries = new Intl.DisplayNames(["fr"], { type: "region" });
const countryName = cc => { try { return countries.of(cc) ?? cc; } catch { return cc; } };
// Le nom d'origine est gardé quand le nom français n'en est qu'un raccourci (« Isle-d'Abeau » pour « L'Isle-d'Abeau »).
const fr = (id, original) => {
  const name = french.get(id)?.name;
  return !name || original.toLowerCase().endsWith(name.toLowerCase()) ? original : name;
};

const rows = cities.map(c => [
  c.lat, c.lon, fr(c.id, c.name), c.region ? fr(c.region.id, c.region.name) : "", c.cc, countryName(c.cc), c.pop,
].join("\t"));
writeFileSync(out, gzipSync(rows.join("\n"), { level: 9 }));
console.log(`${rows.length} villes, ${french.size} noms français → ${out}`);
