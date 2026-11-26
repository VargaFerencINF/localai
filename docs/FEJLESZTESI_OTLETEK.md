# Fejlesztési ötletek

Ide kerülnek azok a kérések, amelyeket **nem kell azonnal megvalósítani**, csak feljegyezni. Megvalósítás előtt
egyeztetjük a sorrendet. Állapot: `ötlet` → `tervezve` → `kész (verzió)`.

---

## 1. Gépre szabott, naprakész modellajánló *(a legfontosabb)*

> Megvalósítva az 1.3.0-ban: gépfelmérés, a programmal szállított modell-katalógus (kiadásonként frissítve),
> illeszkedés + becsült idő/levél + magyar minőség, „Gyors próba” valós méréssel. Nyitott: a katalógus automatikus
> frissítése kiadás nélkül.

**Állapot:** kész (1.3.0) · **Felvéve:** 2026-11-26

**Kérés:** a modellválasztó mindig a **legújabb** modellek közül ajánljon, **arra a gépre szabva**, amelyre a
programot telepítjük.

**Részletek, javaslat:**
- **Gépfelmérés** induláskor és a varázslóban: teljes memória, processzor (magok, AVX2/AVX-512), videókártya
  típusa és saját memóriája (VRAM), NPU, szabad lemezhely.
- **Naprakész modell-lista.** Az ollama.com-nak nincs stabil nyilvános API-ja az ajánláshoz, ezért javasolt egy
  általunk karbantartott `modellek.json` (név, család, méret, memóriaigény, magyar nyelvi minőség, kiadás dátuma,
  utód). Ezt a program letölti, például a GitHub release-ből; ha nincs internet, a beépített lista marad.
- **Illeszkedés pontozása:**
  - elfér-e a memóriában vagy a VRAM-ban;
  - várható sebesség a gépen;
  - magyar minőség;
  - frissesség.

  Az eredmény 2–3 ajánlás indoklással, például: „gyors, ezen a gépen ~1 perc/levél” vagy „pontosabb, ~3 perc/levél”.
- **Gyors próba gomb:** egy rövid mintakérés a kiválasztott modellel, valós sebességméréssel (token/mp,
  másodperc/levél), hogy az ajánlás ne csak becslés legyen.

**Megvalósíthatóság:**
- jól megoldható;
- a hardverfelmérés Windowson WMI-vel megy;
- a nehéz rész a lista naprakészen tartása (kézi karbantartás vagy félautomata frissítés).

---

## 2. Modellek kezelése: törlés, elavult modellek, új verzió

**Állapot:** kész (1.3.0) · **Felvéve:** 2026-11-26

**Kérés:** a gépen már vannak régi modellek, amelyek lehet, hogy elavultak. Lehessen őket **törölni** az
alkalmazásból, és a program próbálja megkeresni az **új verziójukat**.

**Fontos (2026-11-26):** a **sima törlés önmagában is legyen elérhető**, frissítés nélkül. Ha egy régi modellt nem
akarok lecserélni, csak törölni, akkor egy gombbal törölhessem, és a program ne erőltesse az új verzió letöltését.

**Részletek, javaslat:**
- **Letöltött modellek listája:** név, méret, letöltés dátuma, használja-e éppen a program.
- **Törlés gomb minden modell mellett**, megerősítéssel (Ollama `DELETE /api/delete`), a frissítéstől függetlenül.
  Több modell egyszerre is kijelölhető és törölhető. A program által éppen használt modellnél figyelmeztet, és
  felajánlja, hogy előbb átállít egy másikra.
- **„Elavult” jelölés:** ha a modell-listában van utódja (pl. `qwen3:8b` → `qwen3.5:9b`). Mellette
  **„Frissítés az új verzióra”** gomb, amely egy lépésben:
  1. letölti az utódot;
  2. átállítja rá a programot;
  3. felajánlja a régi törlését.
- **Felszabadítható hely összesítése:** „A régi modellek törlésével kb. 14 GB szabadul fel.”

**Megvalósíthatóság:**
- egyszerű, az Ollama API mindent tud hozzá (listázás, törlés, letöltés);
- az utód-párosítás az 1. pont modell-listájából jön.

---

## 3. Erőforrás-figyelő felület (gépterhelés, AI-terhelés)

**Állapot:** kész (1.3.0) · **Felvéve:** 2026-11-26

**Kérés:** legyen egy felület, amely **vizuálisan mutatja a gép fogyasztását**, és hogy **mennyire hajtja meg az AI
modell** a gépet.

**Részletek, javaslat:**
- **Élő grafikonok** (utolsó 5–15 perc):
  - processzor;
  - memória;
  - videókártya (GPU);
  - ha mérhető, NPU.
- **Ollama állapota:** milyen modell van betöltve, mennyi memóriát foglal, processzoron vagy videókártyán fut
  (Ollama `/api/ps`).
- **Feladatonkénti mérés:** token/másodperc és feldolgozási idő minden levélnél (az Ollama válasza tartalmazza);
  napi átlag, leglassabb levél.
- Jelzés, ha a gép túl van terhelve, vagy a modell nem fér el a memóriában, és ezért lassú.

**Megvalósíthatóság:**
- a processzor és a memória Windowson egyszerű (teljesítményszámlálók);
- a videókártya a „GPU Engine” számlálókkal mérhető;
- az NPU-terhelés nem minden gépen érhető el;
- az Ollama adatai az API-ból jönnek;
- új oldal lenne a kezelőfelületen (pl. „Teljesítmény”).

---

## 4. Minden rendelés külön fájlba (alapértelmezésként)

**Állapot:** ötlet · **Felvéve:** 2026-11-26

**Kérés:** minden új rendelés (levél) mindig **külön CSV fájlba** kerüljön, ne egy közös havi fájl végére.

**Részletek, javaslat:**
- Az előrendelés szabály alapértelmezett módja legyen „Levelenként külön fájl” (`per_job`), beszédes fájlnévvel,
  pl. `eloreendeles_2026-11-26_Horgasz-Centrum_<rövid azonosító>.csv` (dátum + partner + egyedi rész).
- Ehhez kell egy új helyettesítő a fájlnévben a partner nevére (ékezet és tiltott karakterek nélkül), és egy rövid
  azonosító a teljes feladat-azonosító helyett.
- Megfontolandó: választható mappaszerkezet (pl. `output\2026\11\`), hogy sok fájl esetén is átlátható maradjon.

**Megvalósíthatóság:** egyszerű.

**Addig is:** már most beállítható kézzel: Beállítások › Kimenet › az előrendelés szabálynál mód: „Levelenként külön
fájl”, fájlnév pl. `eloreendeles_{yyyy}-{MM}-{dd}_{jobId}.csv`.

---

## 5. Áttekintés: a feldolgozott táblázat teljes nézetben

**Állapot:** ötlet · **Felvéve:** 2026-11-26

**Kérés:** az Áttekintésen egy feldolgozott levél eredménye **ne jobbról beúszó panelben** jelenjen meg, hanem
**teljes szélességben, az egész táblázat egyszerre látható** legyen az alkalmazáson belül.

**Részletek, javaslat:**
- Saját oldal egy feladatnak (pl. „Feladat részletei”): felül a levél adatai röviden, alatta a kinyert sorok
  **teljes táblázata** az összes oszloppal (vízszintes görgetés nélkül, amennyire lehet: tördelés, rögzített első
  oszlopok), az ellenőrzendő sorok kiemelve.
- Mellette vagy alatta lenyitható: az eredeti levél szövege, a nyers AI válasz.
- Hasznos lehet: a táblázat letöltése / másolása (CSV vagy vágólap), és később a sorok javítása közvetlenül itt
  (kapcsolódik a v2 „ellenőrző képernyő” ötlethez).
- Opcionálisan az Áttekintés listájában egy sor lenyitva is mutathatná a tételeket.

**Megvalósíthatóság:** egyszerű–közepes (csak a kezelőfelület változik, az adatok már megvannak).

---

## Korábbról (lásd `docs/PROGRESS.md`, „Ideas for v2”)

- Csatolmányok feldolgozása (xlsx, pdf, docx).
- Partner- és cikktörzs egyeztetése ERP-exportból.
- Több postafiók, több szerver.
- Közvetlen ERP-import CSV helyett, ellenőrző képernyő az „ellenőrzendő” sorok javításához.
