# Sebességmérések

Valós futási idők különböző gépeken, hogy a gépválasztást és az időkorlátot (Beállítások › AI (LLM) kapcsolat ›
Időkorlát) mérésekre lehessen alapozni. Az összehasonlíthatóság kedvéért ugyanazt a levelet érdemes futtatni
(lásd lent: „Mérőlevél: nagy rendelés, 50 tétel”).

A token/mp és a sorok száma a Teljesítmény oldalról és a feladat paneljéről olvasható le.

## Eredmények

| Dátum | Gép | Futás helye | Modell | Levél | Idő | Sorok | Token/mp | Megjegyzés |
|---|---|---|---|---|---|---|---|---|
| 2026-11-26 | Laptop: Intel Core Ultra 7 155H, 31,3 GB RAM, Intel Arc (integrált) | processzor | `qwen3.5:9b` | Mérőlevél: nagy rendelés, 50 tétel | **1441 mp (~24 perc)** | 54 | – | Az alap 300 mp-es időkorlát kevés volt, a feladat időtúllépéssel újrapróbálkozott. Minőség: lásd lent. |
| *(tervezett)* | Asztali gép | | `qwen3.5:9b` | Mérőlevél: nagy rendelés, 50 tétel | | | | |

Rövid levelek (2–6 tétel) ugyanezen a laptopon: **~2–3 perc** levelenként.

## Minőség: 2026-11-26, laptop, `qwen3.5:9b`, mérőlevél

**Jól sikerült:**
- **Sorok:** 54 sor (53 valódi tétel és a törölt 26.).
- **Módosítások és mennyiségek:**
  - 15. tétel: 50 tekercs, megjegyezte az eredeti 30-at;
  - két tucat → 24, fél tucat → 6, „ugyanannyi” → 12;
  - S/M és S/M/L/XL bontás helyes.
- **Szállítás:** Kecskemét / 2027-02-15 és Szeged / 2027-03-01 a megfelelő sorokon.
- **Árak:** helyesek; „szokásos áron” és „ha drágult” esetén üres, nem talált ki árat.
- **Feltételes és bizonytalan tételek:**
  - a 8. és az 50. tétel alacsony bizonyosságú → ellenőrzendő;
  - a 25., 39., 42., 48. tétel közepes bizonyosságú, a feltétel a megjegyzésben.

**Hibák:**
- **Törölt tétel:** a 26. tétel („ezt húzd ki”) bekerült 0 mennyiséggel, „ok” státusszal. Ugyanez történt a
  Pecás Paradicsom levélnél a törölt horoggal.
- **Kerekítés:** „a fele” 25-ből 12 lett, nem 12,5, és nem jelölte bizonytalannak.
- **Cikkszám:** az `EF-…` cikkszámok a megnevezésben maradtak, a `cikkszam` oszlop üres.
- **Múltbeli dátum:** egy korábbi levélnél (Horgász Centrum) a „március 1” 2026-03-01 lett, múltbeli dátum.

**Javítás:** a 0 mennyiségű sor ezentúl automatikusan ellenőrzendő (D-1106). A minta prompt kiegészült:
- törölt tétel ne kerüljön be;
- cikkszám kerüljön a cikkszam oszlopba;
- számított mennyiségnél ne kerekítsen;
- évszám nélküli dátumnál a következő jövőbeli dátumot használja.

## Becslések összevetéshez (nem mérés)

| Gép | Becsült írási sebesség | Becsült idő, 50 tételes mérőlevél |
|---|---|---|
| Laptop, processzoron (fenti) | ~6–8 token/mp | 15–25 perc (mért: 24 perc) |
| RTX 3060 12 GB | ~35–45 token/mp | ~3–4 perc |
| AMD 12 GB (RX 7700 XT, ha az Ollama támogatja) | ~30–45 token/mp | ~3–5 perc |

A szöveggenerálás sebességét elsősorban a memória sávszélessége adja meg: minden tokennél a teljes modellt be kell
olvasni. Ezért fontos, hogy a modell teljesen elférjen a videókártya saját memóriájában.

## Mérőlevél: nagy rendelés, 50 tétel

Futtatás előtt az időkorlátot érdemes magasra állítani: processzoron 2400 mp, videókártyán 600 mp is elég.

**Tárgy:** `Fwd: Előrendelés 2027 tavasz – Horgászparadicsom Kft. (3 bolt, nagy lista)`
**Feladó (továbbító):** `Kovács Péter <kovacs.peter@ertekesites.example>`

```
Sziasztok!

Továbbítom Tamás tavaszi listáját, elég hosszú, és ő is írta, hogy menet közben módosított rajta.
Kérlek rögzítsétek, a kérdéses tételeket majd egyeztetem vele.

Péter

-------- Továbbított üzenet --------
Feladó: Varga Tamás <varga.tamas@horgaszparadicsom.example>
Dátum: 2026. 11. 26. 7:42
Címzett: Kovács Péter <kovacs.peter@ertekesites.example>
Tárgy: tavaszi előrendelés – végleges(nek szánt) lista

Szia Péter!

Itt a tavaszi lista, ahogy telefonon megbeszéltük. Az árak nettók, fizetés 30 nap átutalással.
Ha valamelyik ár változott a katalógushoz képest, kérlek szólj, mielőtt visszaigazoljátok.
Minden a kecskeméti központi raktárba menjen február 15-ig, KIVÉVE a kiegészítőket (lásd lent).

BOTOK
1. EF-B3600 Feeder bot 3,6 m 120 g – 12 db – 18 500 Ft/db
2. EF-B3900 Feeder bot 3,9 m 150 g – 8 db – 21 900 Ft/db
3. Method feeder bot 3,3 m – 10 db (a katalógusban 19 990 Ft volt, ha azóta drágult, szólj)
4. Teleszkópos bot 4 m – két tucat
5. Teleszkópos bot 5 m – fél tucat
6. Pergető bot 2,1 m 5-25 g – 6 db – 14 990 Ft
7. Pergető bot 2,4 m 10-40 g – 6 db – 15 990 Ft
8. Bojlis bot 3,6 m 3 lbs – 4 db, de csak akkor, ha a 3,9-es feederből nem tudtok 8-at adni
9. Gyerek horgászszett (bot + orsó) – 15 szett, ezeket a nyári táborra visszük

ORSÓK
10. Nyeletőfékes orsó 5000 – 20 db – 12 490 Ft
11. Nyeletőfékes orsó 6000 – 10 db – 13 990 Ft
12. Elsőfékes orsó 2500 – 12 db
13. Elsőfékes orsó 3000 – ugyanannyi, mint a 2500-asból
14. Tartalék dob az 5000-es orsóhoz – 10 db

ZSINÓROK
15. EF-Z0200 Monofil 0,20 mm 150 m – 30 tek.
16. EF-Z0250 Monofil 0,25 mm 150 m – 40 tek.
17. EF-Z0300 Monofil 0,30 mm 300 m – 20 tek.
18. Fonott zsinór 0,10 mm 135 m – 15 tek. – 4 990 Ft/tek.
19. Fonott zsinór 0,12 mm 135 m – 15 tek. – 4 990 Ft/tek.
20. Fonott zsinór 0,16 mm 135 m – 10 tek. – 5 490 Ft/tek.
21. Fluorocarbon előkezsinór 0,18 mm – 25 tek.
22. Fluorocarbon előkezsinór 0,22 mm – a 0,18-asból kért mennyiség fele

HORGOK, APRÓCIKK
23. EF-H1042 Horog 12-es – 40 cs.
24. EF-H1044 Horog 14-es – 40 cs.
25. EF-H1046 Horog 16-os – 20 cs. (múltkor ebből hiány volt; ha nincs elég, a hiányzó részt 14-esből adjátok)
26. Horog 10-es – ezt húzd ki, mégse kell
27. Forgókapocs 8-as – 50 cs.
28. Gyorskapocs S és M méretben, mindkettőből 30 cs.
29. Gömbólom 10 g – 5 kg
30. Körteólom 20 g – 5 kg
31. Úszó 2 g – 100 db
32. Úszó 4 g – 100 db
33. Gumiütköző – 40 cs.

ETETÉS
34. Etetőanyag ponty-vanília 1 kg – 10 karton (kartononként 12 zacskó), a szokásos áron
35. Etetőanyag eper 1 kg – 6 karton
36. Method mix 800 g – 8 karton
37. Bojli 20 mm eper-hal – 25 kg
38. Bojli 16 mm ananász – 15 kg
39. Pellet 6 mm halas – fél raklap (ha jól emlékszem, az kb. 300 kg)
40. Etetőkosár 30 g – 100 db – 350 Ft
41. Etetőkosár 40 g – 100 db – 390 Ft
42. Method kosár 25 g – 3-4 tucat, ahogy a gyűjtőcsomagolás kijön

KIEGÉSZÍTŐK – ezeket a SZEGEDI boltunkba kérem, március 1-ig (6720 Szeged, Tisza Lajos krt. 45.)
43. EF-SZ220 Horgászszék kartámasszal – 8 db – 24 900 Ft
44. Merítőszák 2,5 m – 6 db
45. Haltartó szák 3 m – 10 db
46. Botzsák 1,5 m – 5 db (feketét kérnénk, de ha nincs, mindegy)
47. Bottartó villa – 20 pár
48. Fejlámpa – 12 db, ha van újabb típus, akkor abból
49. Horgászmellény S, M, L és XL méretből, mindegyikből 2 db
50. Napernyő 2,5 m – 3 db, de csak ha 9 990 Ft alatt van

Még egy módosítás, amit utólag írtam hozzá: a 15. tételből (0,20-as monofil) mégis 50 tekercs kell, nem 30.

A visszaigazolást a beszerzes@horgaszparadicsom.example címre kérném.

Köszönöm!
Varga Tamás
beszerzési vezető
Horgászparadicsom Kft. – Kecskemét · Szeged · Baja
6000 Kecskemét, Halász utca 12.
Tel.: +36 30 000 0000

Ez az e-mail és mellékletei bizalmas információkat tartalmazhatnak. Ha nem Ön a címzett,
kérjük, értesítse a feladót, és törölje az üzenetet.
```

**Elvárt eredmény (ellenőrzéshez):**
- **Sorok:** kb. 52–54 sor.
  - A 26. tétel nem szerepel („ezt húzd ki”).
  - A 28. tétel 2 sor (S, M), a 49. tétel 4 sor (S, M, L, XL).
- **Mennyiségek:**
  - 15. tétel: 50 tekercs (az utólagos módosítás szerint).
  - Két tucat = 24, fél tucat = 6, „ugyanannyi” = 12, „a fele” = 12,5.
- **Ellenőrzendő** (alacsony vagy közepes bizonyosság, a feltétel a megjegyzésben):
  - a feltételes tételek: 8, 25, 48, 50;
  - a bizonytalan mennyiségek: „3-4 tucat”, „fél raklap (kb. 300 kg)”.
- **Szállítás:**
  - 1–42. tétel: Kecskemét, február 15-ig;
  - 43–50. tétel: Szeged, március 1-ig.
- **Árak:** ahol nincs ár („a szokásos áron”, „ha drágult”), ott üres, nem kitalált.
