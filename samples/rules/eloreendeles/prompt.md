## Feladat: előrendelés feldolgozása

Az e-mail egy vevői előrendelés, amelyet egy kolléga továbbított nekünk. A valódi megrendelő a továbbított részben található (Feladó, aláírás, cégnév).

Szabályok:
- Minden rendelt tétel külön sor a "rows" tömbben.
- partner_nev: a megrendelő cég neve (nem a továbbító kolléga). Ha csak személynév van, azt írd be.
- cikk_megnevezes: a termék megnevezése a méretekkel együtt, ahogy az e-mailben szerepel, a mennyiség és az ár nélkül.
- mennyiseg és mennyisegi_egyseg: a megrendelő által használt egységben (db, tekercs, csomag, karton stb.). Ne számold át. Ha a kiszerelés is szerepel (pl. "kartononként 12 db"), azt a megjegyzésbe írd.
- egysegar és penznem: csak ha az ár konkrétan szerepel. "Szokásos ár", "a szokott áron" esetén egysegar null, és ezt a megjegyzésbe írd.
- A közös információkat (pl. szállítási határidő) minden érintett sor megjegyzésébe írd bele.
- Feltételes tétel ("ha van", "még nem biztos"): külön sor, bizonyossag "alacsony", a feltétel a megjegyzésbe kerül.
- Az aláírást, jogi nyilatkozatot és köszönést hagyd figyelmen kívül.
- Homályos, másik rendelésre hivatkozó tétel ("ugyanaz, mint múltkor", "a szokásos mennyiség"): külön sor, a mennyiség csak akkor kerüljön be, ha konkrétan szerepel; a hivatkozást szó szerint írd a megjegyzésbe, bizonyossag "alacsony".
