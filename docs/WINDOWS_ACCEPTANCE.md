# Windows átvételi ellenőrzőlista (valódi szerver)

Ezeket a pontokat csak a valódi Windows-szerveren lehet ellenőrizni (Ollama + Qwen 3.5 9B, Thunderbird, szolgáltatás,
hálózati megosztás, telepítő). A felhős fejlesztőkörnyezetben ezek helyett mock és szimuláció fut (`docs/DEV_MODE.md`).
Minden lépés a saját pontjaival bővíti a listát.

A buildelt `EfMailAgent.exe` a GitHub Actions „build” futásának `EfMailAgent-win-x64` artefaktjaként tölthető le.

## 1. lépés: alapváz, beállítások, szabályok

- [ ] `EfMailAgent.exe validate-rules` (paraméter nélkül, Release build) létrehozza a `C:\ProgramData\EfMailAgent\`
      mappát a `rules`, `output`, `data`, `logs`, `addon` almappákkal és az `appsettings.json` fájllal.
- [ ] Az `appsettings.json` szolgáltatója `ollama`, a `Server.Token` 43 karakteres véletlen érték.
- [ ] Ha a `rules` mappa üres volt, és az exe mellett van `samples\rules` mappa, a mintaszabályok átmásolódnak
      (telepítés előtt ezt a telepítő végzi, lásd 7. lépés).
- [ ] `EfMailAgent.exe print-token` csak a tokent írja ki.
- [ ] A magyar ékezetek helyesen jelennek meg a parancssorban (cmd és PowerShell is), és a naplófájlban
      (`logs\efmailagent-ÉÉÉÉHHNN.log`).
- [ ] `EfMailAgent.exe validate-rules --watch` mellett a `rule.yaml` Jegyzettömbben való módosítása és mentése után
      kb. 1 másodpercen belül újra megjelenik a táblázat, új verziószámmal.
- [ ] Egy szándékosan elrontott `rule.yaml` (pl. `type: decimal`) magyar hibaüzenetet ad, a többi szabály érvényes
      marad, a kilépési kód 1.

## 2. lépés: feldolgozó lánc (sor, worker, AI, CSV)

Előkészítés: az `appsettings.json`-ban `"Llm": { "Provider": "ollama", "BaseUrl": "http://127.0.0.1:11434", "Model": "qwen3.5:9b" }`
(a pontos modellnevet az `ollama list` mutatja).

- [ ] `EfMailAgent.exe test-rule eloreendeles --file samples\emails\demo_eloreendeles_01.eml` értelmes sorokat ad
      (5 tétel, a 3,9 m-es feeder bot `alacsony` bizonyossággal), és semmit nem ír a `output` mappába.
- [ ] Ugyanez a 6 `.txt` mintára, a tárgyat és a feladót a `samples\emails\index.json` alapján megadva
      (`--subject "..." --from "..."`). Feljegyezni: hány tétel jött, mi hibázott, mennyi ideig tartott egy levél.
- [ ] Az Ollama elfogadja a `think: false` mezőt a modellhez (ha HTTP 400 jön emiatt, jelezni kell).
- [ ] A `NumCtx` 16384 belefér a gép memóriájába / GPU-jába (különben csökkenteni, pl. 8192).
- [ ] `test-rule ... --write` után a CSV Excelben helyesen nyílik meg: ékezetek, oszlopok (`;`), tizedesvessző.
- [ ] Zárolt CSV: a CSV nyitva Excelben, `Csv.LockRetrySeconds` = 30, `test-rule ... --write --new-message-id` ->
      kb. 30 mp után `eloreendeles_..._pending_<jobId>.csv` jön létre, figyelmeztetés a naplóban, az eredeti fájl ép.
- [ ] Leállított Ollama mellett `test-rule ... --write --new-message-id` -> „nem érhető el”, a feladat a sorban
      marad (`queued`), nem lesz `failed`.
- [ ] Hálózati megosztás kimenetként (`Paths.Output` = `\\szerver\megosztas\efmailagent`): az írás működik a
      szolgáltatás fiókjával (a szolgáltatás a 7. lépésben készül; addig a saját felhasználóval).
- [ ] (Opcionális) LM Studio: `Provider: openai_compatible`, `BaseUrl: http://127.0.0.1:1234` -> ugyanaz az eredmény.

## 3. lépés: HTTP API

- [ ] `EfMailAgent.exe` paraméter nélkül elindul, a naplóban: „EfMailAgent fut: http://127.0.0.1:47811/api”.
      Windows tűzfal-figyelmeztetés nem jelenik meg (csak a 127.0.0.1-en figyel).
- [ ] Egy másik gépről a `http://<szerver>:47811/api/health` nem érhető el.
- [ ] Böngészőben a `http://127.0.0.1:47811/api/health` 401-et ad (token nélkül).
- [ ] Második példány indítása: „A(z) 127.0.0.1:47811 port foglalt…” üzenet, kilépési kód 5.
- [ ] `.\scripts\dev-submit.ps1 -DataDir <adatmappa>` (Windows PowerShell 5.1-ben és PowerShell 7-ben is) beküldi a demo
      e-mailt, és kiírja a CSV útvonalát; az ékezetek a CSV-ben helyesek.
- [ ] Ctrl+C feldolgozás közben (valódi Ollamával, hosszú válasznál): a feladat `queued` állapotba kerül vissza, és a
      következő indításkor feldolgozódik.
- [ ] A Thunderbird add-on (4. lépés) kéréseinél a naplóban (Debug szinten) látszik az `Origin: moz-extension://…`
      fejléc, és a kérések nem akadnak el CORS miatt (lásd D-303).

## 4. lépés: Thunderbird add-on

Részletes lépések: `docs/TESTING.md`, „Thunderbird add-on kézi tesztje”.

- [ ] A szerveren futó Thunderbird verziója (Súgó > A Thunderbird névjegye): ______. Legalább 128 kell (D-401);
      ha 128 alatti, szólni kell.
- [ ] A `dist\efmailagent.xpi` ideiglenesen betölthető (Eszközök > Fejlesztői eszközök > Kiegészítők hibakeresése),
      a háttérkonzolban nincs hiba vagy manifest-figyelmeztetés (ha van, a szövegét fel kell jegyezni).
- [ ] A beállítások oldalon a token megadása után a „Kapcsolat tesztelése” sikeres; a helyi elérés engedélye megvan
      (vagy az „Engedély megadása” gombbal megadható).
- [ ] A négy címke létrejön a megadott színekkel.
- [ ] Egy továbbított előrendelés „AI: sorban”, majd „AI: kész” / „AI: ellenőrzendő” címkét kap, és a CSV-ben
      megjelennek a sorai.
- [ ] Más tárgyú levél nem kap címkét.
- [ ] Helyi menü: „Küldés AI feldolgozásra > Előrendelés feldolgozás” működik tetszőleges levélre.
- [ ] Leállított exe mellett érkező levél a kimenő sorba kerül, és az exe újraindítása után beküldődik.
- [ ] Thunderbird újraindítása után a visszamenőleges keresés pótolja a kimaradt leveleket.
- [ ] Egy kiválasztott további mappa (beállítások > Figyelt mappák) leveleit is figyeli.
- [ ] Az EfMailAgent naplójában (Debug szinten) az add-on kéréseinél `Origin: moz-extension://…` látszik (D-303).

## 5. lépés: add-on telepítése az exe-ből

A `dist\EfMailAgent.exe` és a `dist\efmailagent.xpi` legyen egy mappában.

- [ ] Rendszergazdai parancssorból: `EfMailAgent.exe install-addon` -> megtalálja a Thunderbirdöt, kiírja a verzióját,
      és létrehozza/frissíti a `C:\Program Files\Mozilla Thunderbird\distribution\policies.json` fájlt (a régi mentése
      `policies.json.bak-…` néven ott marad).
- [ ] Nem rendszergazdai parancssorból ugyanez magyar hibaüzenetet ad (kilépési kód 7), és nem módosít semmit.
- [ ] A Thunderbird újraindítása után az add-on telepítve van (Eszközök > Kiegészítők és témák), nem tiltható le és
      nem távolítható el, és a beállításai oldalán a szerver és a token kitöltve, nem szerkeszthetően látszik
      (a beállítások oldal megnyitása nélkül is működik).
- [ ] `EfMailAgent.exe addon-status` -> „Állapot: RENDBEN”.
- [ ] Új verzió: a `VERSION` emelése után újraépített exe + XPI-vel az `install-addon` (vagy a szolgáltatás
      újraindítása) után a Thunderbird újraindításakor az új add-on verzió települ (D-502).
- [ ] Önjavítás: töröld a `policies.json`-t (vagy frissítsd a Thunderbirdöt), indítsd újra az EfMailAgent-et:
      a naplóban figyelmeztetés, a bejegyzés visszakerül.
- [ ] `EfMailAgent.exe uninstall-addon` -> csak az EfMailAgent bejegyzései tűnnek el a policies.json-ból; a
      Thunderbird újraindítása után az add-on már nem kötelező (a Kiegészítők kezelőjében eltávolítható).
- [ ] Profil mód (ha a policy mód valamiért nem használható): `EfMailAgent.exe install-addon --mode profile`
      (szolgáltatásként futtatva `--user-profile-root C:\Users\<felhasználó>\AppData\Roaming\Thunderbird`) -> a
      Thunderbird indításkor rákérdez az add-onra; a tokent a beállításoknál kell megadni.
