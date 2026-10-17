// Points an overlay config copy at the Phasmophobia reader. Usage: node phasmo-config.js <config.json> <feed path>
// Plain text edits: the config has keys that differ only in case, which JSON readers refuse.
const fs = require("fs");
const [path, feed] = process.argv.slice(2);
let t = fs.readFileSync(path, "utf8");
const set = (key, json) => {
  const re = new RegExp(`"${key}":\\s*("[^"]*"|\\[\\[[^\\]]*\\]\\])`);
  t = re.test(t) ? t.replace(re, () => `"${key}": ${json}`) : t.replace("{", () => `{\n  "${key}": ${json},`);
};
set("GameFeedPath", JSON.stringify(feed));
set("GameProcess", JSON.stringify("Phasmophobia"));
// Ambient_* = always-on beds, truck, menus, the house's random noise spots: never shown
set("SoundNames", JSON.stringify([["^Ambient_", "", ""]]));
fs.writeFileSync(path, t);
console.log("phasmo config written: " + path);
