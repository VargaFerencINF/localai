# Tesztelési útmutató

## Automatikus tesztek (fejlesztőgépen vagy CI-ban)

| Mit | Parancs |
|---|---|
| C# tesztek | `dotnet test` |
| Add-on tesztek (Node 20+) | `cd addon` majd `node --test "test/*.test.js"` |
| Végponttól végpontig szimuláció (exe + szimulált Thunderbird, mock AI) | Linux: `bash scripts/e2e-sim.sh`, Windows: `.\scripts\e2e-sim.ps1` |
| Admin felület használati teszt (böngészőben, Playwright) | `node scripts/ui-test.mjs` (saját példányt indít), vagy futó / telepített szolgáltatáson: `node scripts/ui-test.mjs --url http://127.0.0.1:47811 --token <token>`; képernyőképek: `dist/ui-test` |
| Teljes build (tesztek, XPI, exe) | `.\build.ps1` (eredmény: `dist\EfMailAgent.exe`, `dist\efmailagent.xpi`) |

## Thunderbird add-on kézi tesztje

### Előkészítés

1. Indítsd el az EfMailAgent-et konzol módban: `EfMailAgent.exe` (vagy fejlesztéskor `dotnet run --project src\EfMailAgent`).
   A naplóban meg kell jelennie: „EfMailAgent fut: http://127.0.0.1:47811/api”.
2. Kérd le a tokent: `EfMailAgent.exe print-token`.
3. Készítsd el az XPI-t: `.\build.ps1 -AddonOnly` (eredmény: `dist\efmailagent.xpi`).

### Ideiglenes betöltés

1. Thunderbird: **Eszközök > Fejlesztői eszközök > Kiegészítők hibakeresése** (angolul: Tools > Developer Tools >
   Debug Add-ons), vagy a címsorba: `about:debugging#/runtime/this-firefox`.
2. **Ideiglenes kiegészítő betöltése…** (Load Temporary Add-on…), majd válaszd ki az `addon\manifest.json` fájlt
   (vagy a `dist\efmailagent.xpi`-t). Az ideiglenes add-on a Thunderbird bezárásáig marad betöltve.
3. Az add-on mellett a **Vizsgálat** (Inspect) gomb nyitja meg a háttérszkript konzolját: itt látszanak az
   `[EfMailAgent]` kezdetű naplósorok (beküldés, kimenő sor, szabályfrissítés).
4. Nyisd meg az add-on beállításait (**Eszközök > Kiegészítők és témák > EfMailAgent > Beállítások**):
   - Szerver címe: `http://127.0.0.1:47811`, Token: a `print-token` kimenete. **Mentés**.
   - **Kapcsolat tesztelése**: „Kapcsolat rendben (EfMailAgent 0.1.0, …)” jelenjen meg.
   - Ha sárga dobozban „Engedély megadása” gomb látszik, kattints rá, és engedélyezd a helyi elérést.
   - Ha az exe telepítette az add-ont (5. lépés), a szerver és a token nem szerkeszthető, és ezt egy kék sáv jelzi.

### Mit kell ellenőrizni

- [ ] A címkék létrejönnek: **AI: sorban** (kék), **AI: kész** (zöld), **AI: ellenőrzendő** (sárga), **AI: hiba** (piros).
- [ ] Továbbíts a figyelt postafiókba egy levelet „Előrendelés” tárggyal (pl. a `samples\emails\demo_eloreendeles_01.eml`
      tartalmával). Beérkezés után néhány másodpercen belül **AI: sorban** címkét kap, majd legfeljebb 1 percen belül
      **AI: kész** vagy **AI: ellenőrzendő** lesz (a demo levél ellenőrzendő, mert az utolsó tétel feltételes).
- [ ] Az `output` mappában megjelenik a CSV a sorokkal.
- [ ] Más tárgyú levél (pl. „Szállítási cím változás”) nem kap címkét.
- [ ] Jobb klikk egy tetszőleges levélen az üzenetlistában: **Küldés AI feldolgozásra > Előrendelés feldolgozás**
      beküldi a levelet (a tárgytól függetlenül).
- [ ] Állítsd le az exe-t, és küldj egy újabb előrendelést: a levél **nem** kap címkét, a beállítások oldalán a
      „Kimenő sorban” szám 1. Indítsd újra az exe-t: legfeljebb pár percen belül beküldődik és megkapja a címkét.
- [ ] Zárd be a Thunderbirdöt, küldj be közben egy előrendelést, majd nyisd meg újra: az indításkori
      visszamenőleges keresés (alapból 3 nap) beküldi a címke nélküli levelet.
- [ ] A beállítások oldalon az „Utolsó beküldések” táblázat mutatja a leveleket és az állapotukat.
