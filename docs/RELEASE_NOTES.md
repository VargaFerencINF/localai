# EfMailAgent kiadási megjegyzések

## 1.2.0 (2026-11-26)

### Újdonságok

- **Asztali alkalmazás**: az EfMailAgent saját ablakban nyílik meg (Microsoft Edge / Chrome alkalmazásmód, címsor
  és fülek nélkül, saját tálcaikonnal), fekete konzolablak nélkül. Indítási hiba esetén felugró ablak írja meg az okát.
  Az ablak bezárása után a program a háttérben tovább dolgozik; **Kilépés a programból** gomb a bal oldali sávban.
- **AI telepítő varázsló (Ollama)** a Beállításokban: az Ollama felismerése, letöltése és telepítése egy gombbal
  (aláírás-ellenőrzéssel), modellajánlás a gép memóriája alapján, modell letöltése folyamatjelzővel, majd a program
  átállítása az új modellre.
- **Thunderbird bővítmény telepítése egy gombbal** a Beállításokban, állapotkijelzéssel (rendszergazdai jóváhagyást kér,
  ha kell).

### Ellenőrizve a kiadás előtt

- 323 C# és 85 add-on teszt, végponttól végpontig szimuláció.
- Kezelőfelület használati teszt (49 lépés, böngészőben, hamis Ollamával a varázslóhoz): Linuxon és a telepített
  Windows szolgáltatáson, ahol a Thunderbird bővítmény egy gombos telepítése egy regisztrált Thunderbirdbe is lefut.
- Telepítő Windowson: telepítés, ikonok, frissítés, eltávolítás.

## 1.1.0 (2026-11-26)

### Újdonságok

- **Indításkor mindig megnyílik a kezelőfelület**, bejelentkezve (token beírása nélkül, egyszer használható,
  2 percig érvényes indítási linkkel). Első indításkor, vagy ha az AI nem érhető el, a Beállítások oldal nyílik meg.
- Ha a program már fut (például szolgáltatásként), az újabb indítás csak megnyitja a kezelőfelületet, és nem indít
  egy második példányt.
- **Start menü és asztali ikon: „EfMailAgent kezelőfelület”** (a telepítő hozza létre).
- **Beállítások > Kimenet**: a kimeneti mappa mellett szabályonként beállítható a CSV fájl neve és módja (egy gyűjtő
  fájl vagy levelenként külön fájl), előnézettel arról, hogy milyen nevű fájl keletkezik.
- Az Áttekintés oldalon sárga sáv jelzi, ha az AI nincs beállítva vagy nem érhető el, egy gombbal a Beállításokhoz.

### Javítások

- Az önállóan (telepítő nélkül) indított `EfMailAgent.exe` nem kapta meg a mintaszabályokat („0 érvényes szabály”,
  „Hiányzik a közös rendszerprompt”). A mintaszabályok és a mintalevelek most a programba vannak építve. Aki az
  1.0.0-t már így indította: az 1.1.0 első indításakor az üres szabálymappa automatikusan feltöltődik.
- Ha a program futása közben valaki elindított egy második példányt, annak feldolgozója a portfoglalás észlelése
  előtt elindulhatott, és hozzányúlhatott a sorhoz. A második példány most semmit nem indít el.
- Dupla kattintásos indításkor hiba esetén a konzolablak nyitva marad, így a hiba oka elolvasható.

### Ellenőrizve a kiadás előtt

- 305 C# és 85 add-on teszt, végponttól végpontig szimuláció.
- Kezelőfelület használati teszt (43 lépés, böngészőben): Linuxon és a telepített Windows szolgáltatáson is.
- Windowson: az önálló exe egy üres mappából is megkapja a mintaszabályokat; a telepítő létrehozza a Start menü és az
  asztali ikont (eltávolításkor törli), az `open-admin` működik; telepítés, frissítés, eltávolítás.

## 1.0.0 (2026-11-26)

Az első teljes kiadás.

### Letölthető fájlok

| Fájl | Mire való |
|---|---|
| `EfMailAgent-Setup.exe` | Telepítő a Windows szerverre (szolgáltatás + Thunderbird bővítmény). Ezt kell futtatni. |
| `EfMailAgent.exe` | Önálló program (telepítő nélküli, kézi használatra) |
| `efmailagent.xpi` | A Thunderbird bővítmény külön (a telepítő ezt magától felteszi) |
| `SHA256SUMS.txt` | Ellenőrző összegek |

### Mit tud

- A továbbított, szabad szöveges előrendelő e-mailekből helyi AI-val (Ollama / LM Studio) CSV sorokat készít; a levelek
  nem hagyják el a szervert.
- Thunderbird bővítmény: a tárgy kulcsszavai alapján beküldi a leveleket, címkével jelzi az állapotukat
  (AI: sorban / kész / ellenőrzendő / hiba), pótolja a kimaradt leveleket, kézi beküldés jobb gombbal.
- Szabályok YAML + Markdown fájlokban, újraindítás nélkül szerkeszthetők; új levéltípus felvehető kód nélkül.
- Admin felület a böngészőben (`http://127.0.0.1:47811/admin`): áttekintés, szabályszerkesztő, Tesztkonzol, beállítások,
  napló.
- Megbízható sorkezelés: egyszerre egy levél, újraindítás és AI-kiesés után folytatja, zárolt CSV esetén nem veszít sort.
- Magyar kezelési útmutató: `USER_GUIDE_HU.md` (a programmappában).

### Ellenőrizve a kiadás előtt

- 292 C# és 85 add-on teszt, végponttól végpontig szimuláció.
- Admin felület használati teszt (39 lépés, böngészőben): Linuxon és a telepített Windows szolgáltatáson is.
- Telepítő Windowson: telepítés paraméterekkel, a Thunderbird bővítmény automatikus telepítése, frissítés (beállítások,
  szabályok, CSV megmaradnak), eltávolítás (az adatok megmaradnak).

### Ismert korlátok

- A Thunderbirdnek egy bejelentkezett munkamenetben futnia kell.
- A valódi AI modellel, Thunderbirddel és hálózati megosztással való próba a szerveren végzendő
  (`docs/WINDOWS_ACCEPTANCE.md`).
- A program és a bővítmény nincs digitálisan aláírva (a SmartScreen figyelmeztethet).
