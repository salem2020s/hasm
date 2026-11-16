const fs = require("fs");
const f = "C:/Projects/Hasm-ci/.github/workflows/desktop.yml";
let s = fs.readFileSync(f, "utf8");
if (!s.includes("'.cargoin'")) throw new Error("not found");
s = s.replace("'.cargoin'", "'.cargo\\bin'");
fs.writeFileSync(f, s);
