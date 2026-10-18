export const resources = {
  en: {
    translation: {
      meta: {
        title: "Laundry station simulator",
        description: "Development station simulator for the laundry operations platform.",
      },
      language: { label: "Language", english: "English", swedish: "Swedish" },
      lookup: {
        context: "Development only · synthetic fixtures", title: "Scan-to-order preview",
        boundary: "Preview only. No scan is saved, no working order is set and no receiving is started. Use synthetic test tags only.",
        fixtureLabel: "Test item", tagLabel: "Synthetic RFID tag", action: "Simulate RFID read",
        fixture: { towel: "Towel · demo customer", sheet: "Sheet · demo customer", mop: "Mop · synthetic team", unknown: "Unknown test tag" },
        item: "Item", customer: "Customer", articleType: "Article type",
        article: { towel: "Towel", sheet: "Sheet", mop: "Mop" },
        message: {
          idle: "Choose a test item and simulate a read. Recognition and order matching will run together.", loading: "Recognizing the test item and finding its customer's downloaded orders…",
          recognized: "Synthetic item recognized. Review the customer's downloaded plans below. Nothing has been recorded.",
          changed: "Downloaded plans changed during matching. Simulate the read again; no partial result was kept.",
          limit: "The local preview limit was exceeded. No partial match or automatic order choice was made.",
          notRecognized: "This tag is not recognized in the test catalog. No customer was guessed and nothing was recorded.",
          sourceRequired: "Station setup is required. This browser no longer has a valid station credential.",
          forbidden: "Preview is not permitted. Both scan and order-read permissions are required for this demo installation.",
          disabled: "Scan-to-order preview is not enabled on this gateway. The development features must be enabled explicitly.",
          invalid: "The lookup request was rejected. Try again; if it continues, ask the person responsible for setup.",
          unavailable: "Lookup is unavailable. Check the gateway connection and try again. This is not an unknown-item result.",
        },
      },
      preview: {
        title: "Customer's downloaded plans", notChecked: "Order matching has not been checked.",
        groupBasis: "Grouped by planned receiving date · Today: {{date}} (Stockholm)",
        group: { today: "Today", future: "Upcoming", overdue: "Overdue receiving" }, groupEmpty: "No orders in this group.",
        none: "No orders downloaded for this customer. This does not mean the customer has no orders in the cloud.",
        choose: "Several planned orders match. Choose one to preview; none has been chosen automatically.",
        viewing: "Previewing {{order}} — receiving not started. This does not reserve the shared order.",
        wait: "No working order has been selected.", noRows: "No order previews to show.",
        action: "Preview {{order}}", listLabel: "Matching downloaded plans",
        dates: "Planned receiving: {{receiving}} · Planned delivery: {{delivery}}",
        boundary: "Downloaded plans only. These copies may be out of date; no item has been counted.",
      },
      orders: {
        context: "Read-only · local working copies", title: "Local orders",
        description: "Orders saved at this plant. Refresh reads the gateway’s stored list; it does not download from the cloud.",
        refresh: "Refresh list", restart: "Restart list", more: "Show more",
        activity: "Last download activity: {{time}} (Stockholm)",
        noActivity: "No successful download activity recorded.", activityUnknown: "Download activity has not been checked.",
        listLabel: "Locally stored orders", planned: "Planned", shown: "Orders shown: {{value}}",
        wait: "Waiting for the local order list…", notDisplayed: "Orders are not displayed.",
        empty: "No local orders to show.", notDownloaded: "No orders have been downloaded yet.",
        column: { order: "Order", customer: "Customer", receiving: "Planned receiving", delivery: "Planned delivery", status: "Status" },
        message: {
          loading: "Reading orders from the local gateway…",
          readOnly: "Viewing only. No order is selected or started here. Download activity does not guarantee that these copies are current.",
          sourceRequired: "Station setup is required. This browser no longer has a valid station credential.",
          forbidden: "This station cannot view orders. Ask the person responsible for setup to check its order-read permission and gateway address.",
          disabled: "Order viewing is not enabled on this gateway. No browser permission has been added automatically.",
          changed: "The local list changed. Restart the list before continuing; the rows below belong to the previous view.",
          unavailable: "The order list could not be read. Check the gateway connection and try Refresh list. This does not mean orders were deleted.",
          retained: "The list could not be updated. Previously displayed copies remain below; they have not been checked again.",
        },
      },
      environment: "Development simulator",
      stationTitle: "Laundry station",
      connection: {
        checking: {
          label: "Checking gateway…",
          detail: "Confirming that durable local storage is available.",
        },
        ready: {
          label: "Gateway ready",
          detail: "Local plant storage is available. Cloud status is not checked yet.",
        },
        unavailable: {
          label: "Gateway unavailable",
          detail: "Local storage cannot be confirmed. Scans cannot be saved.",
        },
      },
      enrollment: {
        checking: {
          label: "Checking station setup…",
          detail: "Confirming whether this browser is enrolled with the local gateway.",
        },
        enrolled: {
          label: "Station setup complete",
          detail:
            "This browser is enrolled with the local gateway. Operator sign-in is not implemented yet.",
        },
        notEnrolled: {
          label: "Station setup required",
          detail: "This browser has not been enrolled with the local gateway.",
        },
        unavailable: {
          label: "Station setup unavailable",
          detail: "The gateway answered, but station enrollment could not be checked.",
        },
      },
      actions: {
        retryConnection: "Retry connection",
        settingUpBrowser: "Setting up browser…",
        retrySetup: "Try setup again",
        setupBrowser: "Set up this browser",
      },
      setup: {
        failed: "Station setup could not be completed. Try again.",
        httpsGuidance: "Open this station from the gateway HTTPS address to set up this browser.",
      },
      scan: {
        technologyLabel: "Simulated input technology",
        technology: {
          rfid: {
            name: "RFID",
            context: "Development only · RFID",
            title: "Simulated RFID scan",
            identifierLabel: "Synthetic RFID identifier",
            send: "Send simulated RFID scan",
          },
          barcode: {
            name: "Barcode",
            context: "Development only · Barcode",
            title: "Simulated barcode scan",
            identifierLabel: "Synthetic barcode identifier",
            send: "Send simulated barcode",
          },
        },
        boundary:
          "This records a raw observation only. No operator is signed in, and it does not record receiving, sorting, packing, dispatch, or another laundry operation.",
        saving: "Saving to local gateway…",
        retrySame: "Retry same scan",
      },
      feedback: {
        alreadySaved: {
          label: "Already saved locally",
          detail: "The gateway recognized this unchanged retry and did not duplicate it.",
        },
        synchronized: {
          label: "Saved locally and synchronized",
          detail: "The gateway durably stored this observation and the cloud confirmed it.",
        },
        needsAttention: {
          label: "Saved locally — synchronization needs attention",
          detail: "The observation is safe at this plant, but cloud delivery needs review.",
        },
        saved: {
          label: "Saved locally",
          detail: "The observation is safe at this plant and waiting for cloud synchronization.",
        },
        missingIdentifier: {
          label: "Not saved",
          detail: "Enter a synthetic identifier before sending.",
        },
        unknown: {
          label: "Save result unknown",
          detail: "Retry the same scan. The gateway will not create a duplicate.",
        },
        rejected: {
          label: "Not saved",
          detail: "Check the station connection and identifier, then try again.",
        },
      },
      audit: {
        context: "Read-only diagnostics",
        title: "Synchronization audit",
        description:
          "Shows gateway-to-cloud delivery metadata for this plant. RFID and barcode values are not displayed.",
        refresh: "Refresh audit",
        refreshing: "Refreshing…",
        loading: "Loading synchronization audit…",
        unavailable:
          "The audit snapshot is temporarily unavailable. Locally saved observations are not removed.",
        recentTitle: "Recent observations",
        empty: "No observations have been recorded yet.",
        status: {
          pending: "Waiting",
          synchronized: "Synchronized",
          needsAttention: "Needs attention",
        },
      },
      scopeNote:
        "This Development-only application proves source enrollment and raw scan storage. Operator sign-in and production workflow controls are not implemented.",
    },
  },
  sv: {
    translation: {
      meta: {
        title: "Simulator för tvätteristation",
        description: "Utvecklingssimulator för tvätteriets driftplattform.",
      },
      language: { label: "Språk", english: "Engelska", swedish: "Svenska" },
      lookup: {
        context: "Endast utveckling · syntetiska testdata", title: "Förhandsvisning av orderflöde",
        boundary: "Endast förhandsvisning. Ingen skanning sparas, ingen arbetsorder väljs och ingen inleverans startas. Använd bara syntetiska testtaggar.",
        fixtureLabel: "Testartikel", tagLabel: "Syntetisk RFID-tagg", action: "Simulera RFID-läsning",
        fixture: { towel: "Handduk · demokund", sheet: "Lakan · demokund", mop: "Mopp · syntetiskt team", unknown: "Okänd testtagg" },
        item: "Artikel", customer: "Kund", articleType: "Artikeltyp",
        article: { towel: "Handduk", sheet: "Lakan", mop: "Mopp" },
        message: {
          idle: "Välj en testartikel och simulera en läsning. Identifiering och ordermatchning körs tillsammans.", loading: "Identifierar testartikeln och söker efter kundens nedladdade ordrar…",
          recognized: "Syntetisk artikel identifierad. Granska kundens nedladdade planer nedan. Ingenting har registrerats.",
          changed: "Nedladdade planer ändrades under matchningen. Simulera läsningen igen. Inget ofullständigt resultat har behållits.",
          limit: "Gränsen för lokal förhandsvisning överskreds. Ingen ofullständig matchning eller automatiskt orderval har gjorts.",
          notRecognized: "Taggen känns inte igen i testkatalogen. Ingen kund har gissats och ingenting har registrerats.",
          sourceRequired: "Stationskonfiguration krävs. Den här webbläsaren har inte längre en giltig stationsidentitet.",
          forbidden: "Förhandsvisning tillåts inte. Både skannings- och orderläsbehörighet krävs för demoinstallationen.",
          disabled: "Orderförhandsvisning är inte aktiverad på den här gatewayen. Utvecklingsfunktionerna måste aktiveras uttryckligen.",
          invalid: "Sökförfrågan avvisades. Försök igen. Kontakta den som ansvarar för konfigurationen om problemet kvarstår.",
          unavailable: "Sökning är inte tillgänglig. Kontrollera gatewayanslutningen och försök igen. Detta betyder inte att artikeln är okänd.",
        },
      },
      preview: {
        title: "Kundens nedladdade planer", notChecked: "Ordermatchning har inte kontrollerats.",
        groupBasis: "Grupperat efter planerad inleverans · Idag: {{date}} (Stockholm)",
        group: { today: "Idag", future: "Kommande", overdue: "Försenad inleverans" }, groupEmpty: "Inga ordrar i den här gruppen.",
        none: "Inga ordrar har laddats ned för den här kunden. Det betyder inte att kunden saknar ordrar i molnet.",
        choose: "Flera planerade ordrar matchar. Välj en att förhandsvisa. Ingen har valts automatiskt.",
        viewing: "Förhandsvisar {{order}} — inleverans har inte startats. Den delade ordern reserveras inte.",
        wait: "Ingen arbetsorder har valts.", noRows: "Inga orderförhandsvisningar att visa.",
        action: "Förhandsvisa {{order}}", listLabel: "Matchande nedladdade planer",
        dates: "Planerad inleverans: {{receiving}} · Planerad leverans: {{delivery}}",
        boundary: "Endast nedladdade planer. Kopiorna kan vara inaktuella. Ingen artikel har räknats.",
      },
      orders: {
        context: "Skrivskyddat · lokala arbetskopior", title: "Lokala ordrar",
        description: "Ordrar sparade på den här anläggningen. Uppdatering läser gatewayens sparade lista och hämtar inget från molnet.",
        refresh: "Uppdatera listan", restart: "Börja om listan", more: "Visa fler",
        activity: "Senaste hämtningsaktivitet: {{time}} (Stockholm)",
        noActivity: "Ingen lyckad hämtning är registrerad.", activityUnknown: "Hämtningsaktiviteten har inte kontrollerats.",
        listLabel: "Lokalt sparade ordrar", planned: "Planerad", shown: "Visade ordrar: {{value}}",
        wait: "Väntar på den lokala orderlistan…", notDisplayed: "Ordrar visas inte.",
        empty: "Inga lokala ordrar att visa.", notDownloaded: "Inga ordrar har hämtats ännu.",
        column: { order: "Order", customer: "Kund", receiving: "Planerad inleverans", delivery: "Planerad leverans", status: "Status" },
        message: {
          loading: "Läser ordrar från den lokala gatewayen…",
          readOnly: "Endast visning. Ingen order väljs eller startas här. Hämtningsaktiviteten garanterar inte att kopiorna är aktuella.",
          sourceRequired: "Stationen behöver konfigureras. Webbläsaren har inte längre en giltig stationsidentifiering.",
          forbidden: "Stationen får inte visa ordrar. Be den som ansvarar för konfigurationen kontrollera läsbehörigheten och gatewayadressen.",
          disabled: "Ordervisning är inte aktiverad på gatewayen. Ingen webbläsarbehörighet har lagts till automatiskt.",
          changed: "Den lokala listan har ändrats. Börja om listan för att fortsätta. Raderna nedan tillhör den tidigare visningen.",
          unavailable: "Orderlistan kunde inte läsas. Kontrollera anslutningen och välj Uppdatera listan. Det betyder inte att ordrar har tagits bort.",
          retained: "Listan kunde inte uppdateras. Tidigare visade kopior finns kvar nedan, men har inte kontrollerats igen.",
        },
      },
      environment: "Utvecklingssimulator",
      stationTitle: "Tvätteristation",
      connection: {
        checking: {
          label: "Kontrollerar gateway…",
          detail: "Kontrollerar att beständig lokal lagring är tillgänglig.",
        },
        ready: {
          label: "Gateway är redo",
          detail: "Lokal lagring på anläggningen är tillgänglig. Molnstatus har inte kontrollerats än.",
        },
        unavailable: {
          label: "Gateway är inte tillgänglig",
          detail: "Lokal lagring kan inte bekräftas. Skanningar kan inte sparas.",
        },
      },
      enrollment: {
        checking: {
          label: "Kontrollerar stationskonfiguration…",
          detail: "Kontrollerar om den här webbläsaren är registrerad hos den lokala gatewayen.",
        },
        enrolled: {
          label: "Stationskonfigurationen är klar",
          detail:
            "Den här webbläsaren är registrerad hos den lokala gatewayen. Operatörsinloggning är inte implementerad än.",
        },
        notEnrolled: {
          label: "Stationen måste konfigureras",
          detail: "Den här webbläsaren har inte registrerats hos den lokala gatewayen.",
        },
        unavailable: {
          label: "Stationskonfigurationen är inte tillgänglig",
          detail: "Gatewayen svarade, men stationsregistreringen kunde inte kontrolleras.",
        },
      },
      actions: {
        retryConnection: "Försök ansluta igen",
        settingUpBrowser: "Konfigurerar webbläsaren…",
        retrySetup: "Försök konfigurera igen",
        setupBrowser: "Konfigurera den här webbläsaren",
      },
      setup: {
        failed: "Stationskonfigurationen kunde inte slutföras. Försök igen.",
        httpsGuidance: "Öppna stationen via gatewayens HTTPS-adress för att konfigurera webbläsaren.",
      },
      scan: {
        technologyLabel: "Simulerad inmatningsteknik",
        technology: {
          rfid: {
            name: "RFID",
            context: "Endast utveckling · RFID",
            title: "Simulerad RFID-skanning",
            identifierLabel: "Syntetiskt RFID-ID",
            send: "Skicka simulerad RFID-skanning",
          },
          barcode: {
            name: "Streckkod",
            context: "Endast utveckling · Streckkod",
            title: "Simulerad streckkodsskanning",
            identifierLabel: "Syntetiskt streckkods-ID",
            send: "Skicka simulerad streckkod",
          },
        },
        boundary:
          "Detta registrerar endast en rå observation. Ingen operatör är inloggad och det registrerar inte mottagning, sortering, packning, utleverans eller någon annan tvätteriåtgärd.",
        saving: "Sparar till lokal gateway…",
        retrySame: "Försök med samma skanning igen",
      },
      feedback: {
        alreadySaved: {
          label: "Redan sparad lokalt",
          detail: "Gatewayen kände igen det oförändrade försöket och skapade ingen dubblett.",
        },
        synchronized: {
          label: "Sparad lokalt och synkroniserad",
          detail: "Gatewayen lagrade observationen beständigt och molnet bekräftade den.",
        },
        needsAttention: {
          label: "Sparad lokalt — synkroniseringen behöver ses över",
          detail: "Observationen är säker på anläggningen, men leveransen till molnet behöver granskas.",
        },
        saved: {
          label: "Sparad lokalt",
          detail: "Observationen är säker på anläggningen och väntar på synkronisering med molnet.",
        },
        missingIdentifier: {
          label: "Inte sparad",
          detail: "Ange ett syntetiskt ID innan du skickar.",
        },
        unknown: {
          label: "Okänt om skanningen sparades",
          detail: "Försök med samma skanning igen. Gatewayen skapar ingen dubblett.",
        },
        rejected: {
          label: "Inte sparad",
          detail: "Kontrollera stationsanslutningen och identifieraren och försök sedan igen.",
        },
      },
      audit: {
        context: "Skrivskyddad diagnostik",
        title: "Synkroniseringslogg",
        description:
          "Visar leveransmetadata mellan gatewayen och molnet för den här anläggningen. RFID- och streckkodsvärden visas inte.",
        refresh: "Uppdatera loggen",
        refreshing: "Uppdaterar…",
        loading: "Läser in synkroniseringsloggen…",
        unavailable:
          "Loggen är tillfälligt otillgänglig. Lokalt sparade observationer tas inte bort.",
        recentTitle: "Senaste observationerna",
        empty: "Inga observationer har registrerats än.",
        status: {
          pending: "Väntar",
          synchronized: "Synkroniserad",
          needsAttention: "Behöver åtgärdas",
        },
      },
      scopeNote:
        "Den här utvecklingsapplikationen verifierar källregistrering och lagring av råa skanningar. Operatörsinloggning och produktionsflöden är inte implementerade.",
    },
  },
} as const;

export type SupportedLanguage = keyof typeof resources;
