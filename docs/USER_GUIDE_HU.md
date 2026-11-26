# EfMailAgent: kezelési útmutató

Ez az útmutató a szerver üzemeltetőjének és a szabályokat karbantartó kollégának szól. Programozási tudás nem kell
hozzá.

## 1. Mit csinál a program?

A vevők előrendelései szabad szöveges e-mailben érkeznek („a Horgász Centrumnak kérünk 20 db feeder botot és 5 doboz
csalit”). A kollégák ezeket továbbítják egy erre a célra létrehozott postafiókba. Az EfMailAgent ezekből a levelekből
**automatikusan táblázatot (CSV fájlt) készít**, soronként egy rendelt tétellel, így senkinek nem kell újragépelnie
őket. A CSV fájl Excelben megnyitható, és be lehet tölteni az ERP rendszerbe.

A rendszer két részből áll, mindkettő ugyanazon a Windows szerveren fut:

| Rész | Mit csinál |
|---|---|
| **EfMailAgent szolgáltatás** (Windows szolgáltatás) | Tárolja a szabályokat, sorba állítja a leveleket, egyenként elküldi őket a helyi mesterséges intelligenciának (AI), ellenőrzi a választ, és megírja a CSV fájlt. Van egy admin felülete a böngészőben. |
| **EfMailAgent Thunderbird bővítmény** | Figyeli a beérkező leveleket. Ha a tárgy illik egy szabályra, beküldi a levelet a szolgáltatásnak, és címkével jelzi az állapotát. |

Az AI egy **helyi modell** (Ollama vagy LM Studio) ugyanazon a gépen vagy a belső hálózaton. **A levelek tartalma
nem hagyja el a céget.**

```
 továbbított levél -> Thunderbird + bővítmény -> EfMailAgent szolgáltatás -> helyi AI
                                                        |
                                                        v
                                    C:\ProgramData\EfMailAgent\output\*.csv
```

Egy levél útja:

1. Megérkezik a levél, a Thunderbird letölti.
2. A bővítmény megnézi a tárgyát. Ha szerepel benne egy kulcsszó (pl. „előrendelés”), beküldi, és rárakja az
   **AI: sorban** címkét.
3. A szolgáltatás sorban, egyesével feldolgozza a leveleket (egyszerre mindig csak egyet).
4. Az eredmény sorai a CSV fájl végére kerülnek. A levél címkéje **AI: kész** vagy **AI: ellenőrzendő** lesz
   (hiba esetén **AI: hiba**).

## 2. Telepítés

### 2.1 Előfeltételek

- Windows Server (vagy Windows 10/11) rendszergazdai joggal.
- **Thunderbird** telepítve, a postafiók beállítva, egy bejelentkezett felhasználó alatt **folyamatosan futva**
  (a bővítmény csak futó Thunderbirdben működik; lásd 9. fejezet).
- **Helyi AI szerver**: [Ollama](https://ollama.com) (alapértelmezett cím `http://127.0.0.1:11434`) vagy LM Studio
  (`http://127.0.0.1:1234/v1`), letöltött modellel. Javasolt kiindulás: egy 7-14 milliárd paraméteres, magyarul jól
  értő modell (pl. Qwen), legalább 16 GB RAM-mal, ideálisan GPU-val.

### 2.2 A telepítő futtatása

1. Futtasd rendszergazdaként az `EfMailAgent-Setup.exe` fájlt.
2. A varázsló lépései:
   - **AI szerver típusa**: Ollama vagy OpenAI-kompatibilis (LM Studio).
   - **AI szerver címe és modell neve**: pl. `http://127.0.0.1:11434` és `qwen3.5:9b`. A modell nevét az Ollamában a
     `ollama list` parancs mutatja.
   - **Kimeneti mappa**: ide kerülnek a CSV fájlok (alapértelmezés `C:\ProgramData\EfMailAgent\output`). Lehet
     hálózati megosztás is, de ekkor a szolgáltatás fiókjának írási jog kell rá (lásd lent).
   - **Szolgáltatás fiókja**: üresen hagyva a beépített `LocalSystem` fiók. Hálózati megosztáshoz adj meg egy
     tartományi fiókot és jelszót.
3. A telepítő:
   - létrehozza a `C:\ProgramData\EfMailAgent\` adatmappát (beállítások, szabályok, sor, napló);
   - generál egy titkos **tokent** (ezzel azonosítja magát a bővítmény);
   - telepíti és elindítja a **EfMailAgent** Windows szolgáltatást (automatikus, késleltetett indítás);
   - telepíti a Thunderbird bővítményt (a Thunderbird `distribution\policies.json` fájljába bejegyzi, a meglévő
     beállításokat megtartva, az eredetiről biztonsági másolatot készít).
4. **Indítsd újra a Thunderbirdöt.** A bővítmény ezután megjelenik az Eszközök > Kiegészítők listában, és nem lehet
   kikapcsolni (a szabályzat telepítette).
5. A telepítő végén megnyílik a kezelőfelület (bejelentkezve). A Start menüben és az asztalon létrejön az
   **EfMailAgent kezelőfelület** ikon: ezzel bármikor újra megnyitható.

Felügyelet nélküli telepítés (pl. távoli telepítéshez):

```
EfMailAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /LLMURL=http://127.0.0.1:11434 /LLMMODEL=qwen3.5:9b /OUTPUT=D:\csv
```

További paraméter: `/LLMPROVIDER=openai_compatible`. Felügyelet nélküli telepítésnél a szolgáltatás a `LocalSystem`
fiókkal fut; más fiókot utólag az `EfMailAgent.exe install-service --account ... --password ...` paranccsal lehet
beállítani.

### 2.3 A program indítása: asztali alkalmazásként

Az EfMailAgent **asztali alkalmazásként** nyílik meg: saját ablakban (címsor és böngészőfülek nélkül, saját
tálcaikonnal), bejelentkezve, token beírása nélkül.

- **Start menü / asztal: EfMailAgent kezelőfelület** ikon. Ha a program (vagy a szolgáltatás) már fut, csak
  megnyitja az ablakot; ha nem fut, el is indítja.
- **Az `EfMailAgent.exe` dupla kattintással** (telepítő nélkül is): a program elindul, és megnyílik az ablaka. Fekete
  konzolablak nem marad nyitva; ha valami miatt nem tud elindulni, felugró ablak írja meg az okát.
- Az ablak **bezárása után a program a háttérben tovább dolgozik** (a beérkező leveleket feldolgozza); az ikonnal
  bármikor újra megnyitható. Teljes leállítás: a bal oldali sávban **Kilépés a programból**. (Ha a program Windows
  szolgáltatásként fut, ez a gomb nem látszik: a szolgáltatás mindig fut.)
- Első indításkor, vagy ha az AI nem érhető el, a **Beállítások** oldal nyílik meg (2.4 fejezet).

Az ablakhoz a gépen lévő **Microsoft Edge** kell (Windows 10/11-en és Windows Server 2022-n alapból megvan); ha nincs,
Google Chrome, végső esetben az alapértelmezett böngésző nyílik meg. A felület címe **http://127.0.0.1:47811/admin**
(csak ezen a gépen érhető el); kézzel megnyitva a tokennel lehet belépni:
`"C:\Program Files\EfMailAgent\EfMailAgent.exe" print-token`.

### 2.4 Első beállítás a Beállítások oldalon (varázsló)

**1. AI telepítése és modellválasztás (Ollama)** – a Beállítások oldal tetején:

1. **Ollama (helyi AI szerver)**: a program megnézi, fut-e az Ollama. Ha nem, a **Ollama letöltése és telepítése**
   gomb letölti a hivatalos telepítőt az ollama.com-ról, ellenőrzi a digitális aláírását, és elindítja (kb. 1 GB,
   rendszergazdai jog nem kell). A folyamatjelző mutatja, hol tart; a végén megvárja, hogy az Ollama elinduljon.
   - Ha az EfMailAgent Windows szolgáltatásként fut, a telepítőt nem tudja a te asztalodon elindítani: ilyenkor a
     **Az Ollama letöltése (ollama.com)** linkről töltsd le és telepítsd, majd kattints az **Újraellenőrzés** gombra.
   - Ha az Ollama másik gépen fut, írd be a címét (pl. `http://192.168.1.20:11434`), és **Újraellenőrzés**.
2. **Modell letöltése és kiválasztása**: a lista mutatja az ajánlott modelleket (méret, szükséges memória), a gép
   memóriája alapján egy **„ajánlott ehhez a géphez”** jelöléssel, és a már letöltött modelleket. Más modell neve is
   beírható (az ollama.com/library oldalon található nevek). Válassz egyet, majd **… letöltése** (folyamatjelzővel;
   egy 5 GB-os modell a hálózattól függően 5–30 perc), végül **Használat ezzel a modellel**: a program azonnal erre a
   modellre áll át. A **Tesztkonzolban** ki is próbálhatod.

**2. Thunderbird bővítmény** – a Beállítások oldalon lejjebb: az állapot (talált Thunderbird, telepített verzió) és a
**Telepítés a Thunderbirdbe** gomb. Egy kattintással telepíti a bővítményt a gépen lévő Thunderbirdbe, a szerver
címét és a tokent is beállítja. Ha a program nem rendszergazdaként fut, a Windows engedélyt kér (válaszd az Igen
gombot). Utána **zárd be teljesen és indítsd újra a Thunderbirdöt**.

**3. Kimenet** (7.0 fejezet): hova és milyen nevű CSV fájlokba kerüljenek a sorok.

**Ellenőrzés:** az **Áttekintés** oldalon az AI zöld („elérhető”), a Thunderbird bővítmény „rendben”; a
Thunderbirdben a bővítmény beállításainál a **Kapcsolat tesztelése** zöld. Küldj egy próbalevelet „Előrendelés
teszt” tárggyal: pár perc múlva megjelenik a CSV-ben.

### 2.5 Szolgáltatásfiók és hálózati megosztás

- A `LocalSystem` fiók a hálózaton a **számítógépfiókként** (`TARTOMANY\SZERVERNEV$`) jelenik meg, ezért
  megosztásra csak akkor tud írni, ha a gépfióknak van rá joga. Egyszerűbb egy külön tartományi fiók (pl.
  `CEG\efmailagent`), amelynek írási joga van a megosztáshoz; a `C:\ProgramData\EfMailAgent` mappához a telepítő
  adja meg a jogot.
- Ha az AI szerver másik gépen fut, a szolgáltatás fiókjának el kell érnie azt a hálózaton (tűzfal).

## 3. Levelek továbbítása és a tárgy kulcsszavai

A kollégák **továbbítják** (Továbbítás / Forward) a vevő levelét a feldolgozó postafiókba. A levél **tárgyában**
szerepelnie kell egy kulcsszónak. Az előrendelés szabály kulcsszavai alapból:

- `előrendelés` (ékezet nélkül is jó: `elorendeles`)
- `pre-order`

Szabályok a tárgy vizsgálatára:

- A kis- és nagybetű nem számít, az ékezet sem (`ELŐRENDELÉS`, `Elorendeles` egyaránt jó).
- A „Fwd:”, „Re:”, „Tov:”, „VS:”, „[EXT]” és hasonló előtagok nem zavarnak.
- A kulcsszó a tárgy bármely részén lehet: „Fwd: Horgász Centrum előrendelés tavaszra” is illeszkedik.

**Kézi beküldés**: ha egy levél tárgyában nincs kulcsszó, a Thunderbird levéllistájában jobb gomb >
**Küldés AI feldolgozásra** > válaszd ki a szabályt.

**Fontos a továbbításnál**: ne töröld a továbbított rész fejlécét („Feladó:”, „From:”), mert abból ismeri fel a
program az eredeti megrendelőt (a CSV `eredeti_kuldo` oszlopa).

Ha a Thunderbird egy ideig nem futott (pl. a szerver újraindult), induláskor a bővítmény **visszamenőleg átnézi az
utolsó 3 nap** címke nélküli leveleit, és beküldi a kimaradtakat. Ugyanazt a levelet a rendszer kétszer nem dolgozza
fel (a Message-ID alapján felismeri).

## 4. Új levéltípus felvétele (kidolgozott példa)

Tegyük fel, hogy a **reklamációkat** is táblázatba szeretnénk gyűjteni: „Reklamáció” tárgyú levelek, amelyekből a
partner, a termék, a hibás mennyiség és a hiba leírása kell.

### 4.1 Új szabály az admin felületen

1. Admin felület > **Szabályok** > **Új szabály**. Azonosító: `reklamacio` (kisbetű, ékezet és szóköz nélkül).
   A program létrehozza a `C:\ProgramData\EfMailAgent\rules\reklamacio\` mappát egy kiinduló `rule.yaml` és
   `prompt.md` fájllal. (Gyorsabb, ha egy meglévő szabályról **Másolat készítése** gombbal indulsz.)
2. A `rule.yaml` fülön írd át:

```yaml
id: reklamacio
name: Reklamáció feldolgozás
enabled: true
priority: 20
match:
  subject_contains: ["reklamáció", "reklamacio", "panasz"]
  subject_regex: null
prompt_file: prompt.md
skills:
  - skills/partnerek.md
output:
  mode: append
  file: "reklamacio_{yyyy}-{MM}.csv"
  rows_path: rows
  columns:
    - { name: partner_nev,      type: string,  required: true,  description: "A reklamáló partner neve" }
    - { name: cikk_megnevezes,  type: string,  required: true,  description: "A hibás termék megnevezése" }
    - { name: hibas_mennyiseg,  type: number,  required: false, description: "Hány darab hibás, számként" }
    - { name: szamlaszam,       type: string,  required: false, description: "Számla- vagy szállítólevélszám, ha szerepel" }
    - { name: hiba_leirasa,     type: string,  required: true,  description: "A hiba rövid leírása" }
    - { name: kert_megoldas,    type: enum, values: [csere, javitas, jovairas, egyeb], required: false, description: "Mit kér a partner" }
    - { name: bizonyossag,      type: enum, values: [magas, kozepes, alacsony], required: true, description: "Mennyire biztos a sor helyessége" }
```

   Mentéskor a program ellenőrzi a fájlt; hibás szabályt nem ment el, és magyarul megmondja, mi a hiba.

3. A `prompt.md` fülön írd le emberi nyelven a feladatot, mintha egy új kollégának magyaráznád:

```markdown
## Feladat: reklamáció feldolgozása

Az e-mail egy vevői reklamáció, amelyet egy kolléga továbbított nekünk.

Szabályok:
- Minden reklamált termék külön sor.
- partner_nev: a reklamáló cég neve (nem a továbbító kolléga).
- hiba_leirasa: egy-két mondatban, a partner szavaival.
- Ha a partner nem írja, mit kér, a kert_megoldas legyen null.
- Ha nem egyértelmű, melyik termékről van szó, a bizonyossag legyen "alacsony".
```

4. Kapcsold be a szabályt (kapcsoló a lista mellett). **Újraindítás nem kell**: a változás azonnal él, a
   Thunderbird bővítmény legkésőbb 15 percen belül letölti az új kulcsszavakat.

### 4.2 Mezőtípusok a `columns` listában

| Típus | Jelentés | CSV-ben |
|---|---|---|
| `string` | szöveg | ahogy van |
| `number` | szám (tizedes is) | tizedesvesszővel, pl. `12,5` |
| `integer` | egész szám | `12` |
| `date` | dátum | `2026-12-01` |
| `boolean` | igen/nem | `true` / `false` |
| `enum` | csak a `values` listában szereplő érték | pl. `csere` |

`required: true`: a mezőnek mindig ki kell töltve lennie; ha üres, a sor „ellenőrzendő” lesz.
`required: false`: ha az e-mailben nincs adat, a cella üres marad. A `description` szöveget az AI is megkapja,
ezért érdemes pontosan megfogalmazni.

Ha több szabály kulcsszava is illik a tárgyra, a kisebb `priority` számú nyer.

## 5. Finomhangolás a Tesztkonzollal

A **Tesztkonzol** oldalon egy levelet úgy futtathatsz le egy szabályon, hogy **semmi nem kerül a CSV-be**:

1. Válaszd ki a szabályt.
2. Másold be a levél szövegét (vagy tölts be egy mintalevelet), add meg a tárgyat és a feladót.
3. **Futtatás**. Jobb oldalt látod:
   - az eredményt (zöld: rendben, sárga: van ellenőrzendő sor, piros: hibás válasz),
   - a felismert eredeti feladót,
   - a sorokat táblázatban,
   - lenyitható részben a **nyers AI választ** és a **teljes promptot**, amit az AI kapott.
4. Ha egy mezőt rosszul tölt ki az AI, javíts a `prompt.md`-n vagy a mező `description` szövegén, mentsd, és futtasd
   újra. Addig ismételd, amíg a jellemző levelekre jó az eredmény.

Tippek:

- Egy szabály egy dolgot írjon le, rövid, egyértelmű mondatokkal. A „ne találj ki adatot” szabály a közös
  rendszerpromptban (`rules\_base\system.md`) már benne van.
- Példát adni hatásos: „Ha azt írja, »a szokásos áron«, az egysegar legyen null, és ezt írd a megjegyzésbe.”
- Gyűjts 5-10 valódi, jellemző levelet, és mindegyiken próbáld ki a változtatást.
- A szerveren a parancssori `EfMailAgent.exe test-rule <szabaly> <levelfajl>` ugyanezt teszi.

## 6. Partner- és cikklista mint „skill”

A skill egy kiegészítő szövegfájl, amelyet az AI a szabály promptja után megkap (pl. partnerlista kódokkal,
cikklista, mértékegységek). Az előrendelés szabálynál két skill van: `mertekegysegek.md` és `partnerek.md`.

Partnerlista feltöltése:

1. Admin felület > **Szabályok** > `eloreendeles` > `skills/partnerek.md` fül.
2. A „A lista jelenleg üres.” sor helyére másold be a partnereket soronként, `név;kód` formában, pl. az ERP-ből
   exportált listából:
   ```
   Horgász Centrum Kft.;P00123
   Pecás Paradicsom Bt.;P00456
   ```
3. Mentsd. Ezután az AI kitölti a `partner_kod` oszlopot, ha a partner egyértelműen azonosítható.

Új skill: a szabály oldalán a **+ Skill fájl** gomb. A program létrehozza a fájlt a `skills\` mappában, és
fel is veszi a `rule.yaml` `skills:` listájába.

Megjegyzés: a skill a promptba kerül, ezért nagyon hosszú lista (több ezer sor) lassítja a feldolgozást, és a modell
memóriájába (kontextus) sem biztos, hogy belefér. Ilyenkor csak a gyakori partnereket tedd bele.

## 7. A CSV fájl

### 7.0 Hova kerülnek a fájlok (Beállítások > Kimenet)

A kezelőfelület **Beállítások** oldalán, a **Kimenet: hova kerülnek a CSV fájlok** részben:

- **Kimeneti mappa**: minden CSV ide kerül, pl. `D:\CSV` vagy egy hálózati megosztás (`\\szerver\megosztas\csv`).
  Mentéskor a program ellenőrzi, hogy a mappa írható-e. A változás azonnal érvényes, újraindítás nem kell.
- **CSV fájlok szabályonként**: minden szabálynál (levéltípusnál) megadható
  - a **mód**: *Egy gyűjtő fájl* (a sorok ugyanabba a fájlba kerülnek egymás után) vagy *Levelenként külön fájl*;
  - a **fájlnév**, helyettesítőkkel: `{yyyy}` év, `{MM}` hónap, `{dd}` nap, `{ruleId}` a szabály azonosítója,
    `{jobId}` a feladat azonosítója. Például `eloreendeles_{yyyy}-{MM}.csv` havonta új fájl,
    `eloreendeles_{yyyy}-{MM}-{dd}.csv` naponta új fájl, `rendeles_{jobId}.csv` levelenként egy fájl.
  - A sor alatt látszik, pontosan milyen nevű fájl keletkezik ma. Mappát a név nem tartalmazhat. **Mentés**
    szabályonként.

### 7.1 Formátum

- Hely: a kimeneti mappa (alapból `C:\ProgramData\EfMailAgent\output\`), pl. `eloreendeles_2026-11.csv`
  (havonta új fájl, a név a Beállításokban módosítható).
- UTF-8 (BOM-mal, így az Excel helyesen mutatja az ékezeteket), pontosvessző elválasztó, tizedesvessző.
- Excelben dupla kattintással megnyitható.
- Biztonság: ha egy szöveges cella `=`, `+`, `-` vagy `@` jellel kezdődne, a program egy `'` jelet tesz elé, hogy az
  Excel ne képletként értelmezze (beállítás: `Csv.EscapeFormulas`).

### 7.2 Oszlopok

Minden sor elején ugyanazok az oszlopok állnak, utánuk a szabály saját oszlopai:

| Oszlop | Jelentés |
|---|---|
| `job_id` | A feladat azonosítója (az admin felületen ezzel kereshető) |
| `feldolgozva` | Mikor dolgozta fel a program (`2026-11-26 14:05`) |
| `email_datum` | Mikor érkezett a levél |
| `kuldo` | Aki a levelet a postafiókba küldte (a továbbító kolléga) |
| `eredeti_kuldo` | A felismert eredeti feladó (a vevő), üres, ha nem található |
| `targy` | A levél tárgya |
| `message_id` | A levél egyedi azonosítója |
| `statusz` | `ok` vagy `ellenorzendo` |

Az előrendelés szabály oszlopai: `partner_nev`, `partner_kod`, `cikk_megnevezes`, `cikkszam`, `mennyiseg`,
`mennyisegi_egyseg`, `egysegar`, `penznem`, `kert_szallitasi_datum`, `megjegyzes`, `bizonyossag`.

### 7.3 Sor- és levélállapotok

| Sor `statusz` | Mikor |
|---|---|
| `ok` | Minden kötelező mező ki van töltve, a bizonyosság nem alacsony |
| `ellenorzendo` | Az AI „alacsony” bizonyosságot adott, vagy egy kötelező mező üres. **Ember nézze meg.** |

| Levél (feladat) állapota | Thunderbird címke | Jelentés |
|---|---|---|
| sorban (`queued`) | AI: sorban | Vár a feldolgozásra |
| feldolgozás alatt (`processing`) | AI: sorban | Az AI éppen dolgozik rajta |
| kész (`done`) | AI: kész | Minden sor `ok` |
| ellenőrzendő (`needs_review`) | AI: ellenőrzendő | Van `ellenorzendo` sor, vagy nem talált tételt |
| hiba (`failed`) | AI: hiba | Az AI válasza a javító kérés után sem felelt meg a szabály mezőinek, vagy az AI szerver többször egymás után hibát adott / túllépte az időkorlátot |

Az **Áttekintés** oldalon az **Ellenőrzendők megnyitása** gomb kilistázza az ellenőrzendő feladatokat. Egy feladatra
kattintva látod a sorokat és az okot, a **Újrafuttatás** gombbal újra feldolgozható (pl. a prompt javítása után).
Újrafuttatáskor az új sorok ismét a CSV végére kerülnek; a régieket kézzel kell törölni.

Naponta egyszer (éjfél után) a napló egy összesítő sort ír: „Napi összesítő (2026-11-26): feldolgozva 42 (kész 30,
ellenőrzendő 11, hiba 1), átlagos feldolgozási idő 38,2 mp, jelenleg a sorban 0”.

## 8. Hibaelhárítás

| Tünet | Valószínű ok | Teendő |
|---|---|---|
| **A bővítmény nem látszik a Thunderbirdben** | A Thunderbird nem lett újraindítva, vagy a szabályzat nincs bejegyezve | Zárd be teljesen a Thunderbirdöt (tálcáról is), és indítsd újra. Parancssorban: `EfMailAgent.exe addon-status`. Ha hibát jelez: `EfMailAgent.exe install-addon` (rendszergazdaként), majd Thunderbird újraindítás. Ha a szabályzat nem működik: `EfMailAgent.exe install-addon --mode profile`. |
| **A címkék nem változnak** („AI: sorban” marad) | A szolgáltatás nem fut, vagy az AI lassú / elérhetetlen | Admin felület > Áttekintés: hány levél vár, fut-e a feldolgozás. Szolgáltatások (`services.msc`) > EfMailAgent fut-e. A bővítmény 60 másodpercenként frissít; ha a Thunderbird be volt zárva, induláskor pótolja. |
| **Egy levél nem kapott címkét** | A tárgyban nincs kulcsszó, vagy a levél nem figyelt mappába érkezett | Ellenőrizd a tárgyat (3. fejezet). Más mappát a bővítmény beállításaiban lehet hozzáadni. Kézi beküldés: jobb gomb > Küldés AI feldolgozásra. |
| **„AI nem elérhető” az Áttekintésen** | Az Ollama / LM Studio nem fut, rossz cím vagy modellnév | Indítsd el az AI szervert. Böngészőben nyisd meg `http://127.0.0.1:11434` (Ollama: „Ollama is running”). Beállítások oldal: cím, modell, **Kapcsolat tesztelése**. A levelek közben nem vesznek el: a sor megvárja, amíg az AI újra elérhető. |
| **A CSV-be nem kerül új sor, a naplóban „zárolt fájl”** | A CSV meg van nyitva Excelben | Zárd be az Excelt. A program 10 percig próbálkozik, utána a sorokat egy külön `<fájlnév>_pending_<azonosító>.csv` fájlba írja ugyanabba a mappába; ennek tartalmát kézzel kell a fő fájlhoz másolni. **Sor nem vész el.** Tipp: a CSV-t másolatként nyisd meg, vagy csak olvasásra. |
| **„Érvénytelen token” / a Kapcsolat tesztelése 401-et ír** | A bővítmény régi tokent használ (pl. újratelepítés után) | `EfMailAgent.exe install-addon` újra beírja a helyes tokent, majd Thunderbird újraindítás. Kézi beállításnál: `EfMailAgent.exe print-token`, és másold be a bővítmény beállításaiba. |
| **Sok „hiba” állapotú levél** | A modell gyenge vagy túl kicsi a kontextus a hosszú prompthoz | Tesztkonzolban nézd meg a nyers választ. Próbálj nagyobb modellt, vagy rövidítsd a skill listákat. |
| **Lassú feldolgozás** | Nincs GPU, túl nagy modell | `EfMailAgent.exe benchmark eloreendeles` megméri a levelenkénti időt. Kisebb modell vagy GPU segít. |
| **Hibás szabály** | Elgépelés a `rule.yaml`-ban kézi szerkesztés után | Az admin Szabályok oldala piros jelzéssel mutatja a hibát; a többi szabály tovább működik. Parancssorban: `EfMailAgent.exe validate-rules`. |
| **Dupla kattintás után a konzolablak hibát ír és Entert vár** | Az adatmappa nem írható, vagy a beállítások hibásak | Olvasd el a hiba okát az ablakban. Ha a szolgáltatás telepítve van, a kezelőfelületet a Start menü **EfMailAgent kezelőfelület** ikonjával nyisd meg. |
| **A kezelőfelület böngészőfülben nyílik meg, nem külön ablakban** | Nincs Microsoft Edge / Chrome a gépen | Telepítsd a Microsoft Edge-et; addig a felület a böngészőben is teljes értékű. |
| **Az Ollama telepítése / modell letöltése nem indul** | Nincs internetkapcsolat az ollama.com felé, vagy a program szolgáltatásként fut | Ellenőrizd a hálózatot / proxyt. Szolgáltatásnál töltsd le az Ollamát kézzel (link a varázslóban). Modellt a varázsló szolgáltatásként is le tud tölteni. |
| **„Telepítés a Thunderbirdbe”: a Windows engedélykérést elutasították** | A felugró ablakban a Nem gombot választották | Kattints újra, és válaszd az Igen gombot (rendszergazdai jog kell a Thunderbird mappájához). |
| **„Az indítási link lejárt”** a belépő oldalon | A bejelentkező link 2 percig és csak egyszer érvényes (pl. a böngésző fülét később frissítetted) | Nyisd meg újra a Start menü ikonnal, vagy lépj be a tokennel (`print-token`). |
| **A szolgáltatás nem indul** | Foglalt port (47811) vagy hibás `appsettings.json` | Nézd meg a naplót: `C:\ProgramData\EfMailAgent\logs\`. Konzolos próba: `EfMailAgent.exe` (paraméter nélkül) kiírja a hibát. |

Naplók: `C:\ProgramData\EfMailAgent\logs\` (naponta új fájl), illetve az admin felület **Napló** oldala. A levelek
teljes szövege alapból nem kerül a naplóba.

## 9. Fontos üzemeltetési tudnivalók

- **A Thunderbirdnek futnia kell** egy bejelentkezett felhasználói munkamenetben. Ha a szerver újraindul, valakinek
  be kell jelentkeznie, és el kell indítania a Thunderbirdöt (érdemes automatikus bejelentkezést és a Thunderbirdöt
  az Indítópultba tenni). A kimaradt leveleket induláskor pótolja (utolsó 3 nap).
- A szolgáltatás magától indul a Windows-zal, a Thunderbird nélkül is fut (a beérkező levelekhez viszont kell).
- A feldolgozás **szigorúan egyesével** történik; 100 levél egy lassú gépen órákig is tarthat, de egyik sem vész el,
  és a sor a szolgáltatás újraindítását is túléli.
- Az AI-nak minden levél „adat”, nem utasítás: ha egy levélben az áll, hogy „hagyd figyelmen kívül az utasításokat”,
  az nem változtat a CSV formátumán (a program a választ mindig a szabály mezői alapján ellenőrzi).

## 10. Mentés és frissítés

### Mentés

Ezt a mappát kell rendszeresen menteni: `C:\ProgramData\EfMailAgent\`

| Almappa | Tartalom | Fontosság |
|---|---|---|
| `appsettings.json` | beállítások, token | fontos |
| `rules\` | a szabályok (a te munkád!) | **nagyon fontos** |
| `output\` | a CSV fájlok (ha nem máshova állítottad) | **nagyon fontos** |
| `data\queue.db` | a feladatok listája és előzményei | hasznos |
| `logs\` | napló | nem szükséges |

A `queue.db` mentéséhez érdemes előtte leállítani a szolgáltatást (`EfMailAgent.exe stop-service`), vagy a
mentőprogram árnyékmásolatát (VSS) használni.

### Frissítés új verzióra

1. Futtasd az új `EfMailAgent-Setup.exe`-t rendszergazdaként.
2. A telepítő felismeri a meglévő telepítést: a beállításokat, a tokent, a szabályokat és a CSV fájlokat
   **megtartja**, a sorban álló levelek a frissítés után folytatódnak.
3. Ha a bővítmény is frissült, indítsd újra a Thunderbirdöt.

A Thunderbird frissítése után a szolgáltatás induláskor ellenőrzi, hogy a bővítmény bejegyzése megvan-e, és ha
kell, helyreállítja.

### Eltávolítás

Vezérlőpult > Programok > EfMailAgent > Eltávolítás. A telepítő megkérdezi, törölje-e az adatmappát (alapból
**nem** törli, így a szabályok és a CSV fájlok megmaradnak).

## 11. Parancssori összefoglaló

A parancsok a telepítési mappában (`C:\Program Files\EfMailAgent\`) futtathatók: `EfMailAgent.exe <parancs>`.
A `--help` minden parancsnál magyar leírást ad.

| Parancs | Mire jó |
|---|---|
| `print-token` | Kiírja a tokent |
| *(paraméter nélkül)* | Elindítja a programot és megnyitja a kezelőfelületet; ha már fut, csak a felületet nyitja meg (`--no-browser`: böngésző nélkül) |
| `open-admin` | Megnyitja a kezelőfelületet (asztali ablakban), bejelentkezve |
| `validate-rules` | Ellenőrzi a szabályokat |
| `test-rule <szabaly> <fajl>` | Egy levelet lefuttat egy szabályon (CSV írás nélkül) |
| `benchmark <szabaly>` | Megméri a feldolgozási időt a mintaleveleken |
| `addon-status` | A Thunderbird bővítmény telepítési állapota |
| `install-addon` / `uninstall-addon` | Bővítmény telepítése / eltávolítása (rendszergazda) |
| `install-service` / `uninstall-service` / `stop-service` | A Windows szolgáltatás kezelése (rendszergazda) |
| `configure --llm-base-url ... --llm-model ...` | Beállítások módosítása parancssorból |
