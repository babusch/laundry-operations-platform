export const resources = {
  en: {
    translation: {
      meta: {
        title: "Laundry station simulator",
        description: "Development station simulator for the laundry operations platform.",
      },
      language: { label: "Language", english: "English", swedish: "Swedish" },
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
        context: "Development only · Barcode",
        title: "Simulated barcode scan",
        boundary:
          "This records a raw observation only. No operator is signed in, and it does not record receiving, sorting, packing, dispatch, or another laundry operation.",
        identifierLabel: "Synthetic barcode identifier",
        saving: "Saving to local gateway…",
        retrySame: "Retry same scan",
        send: "Send simulated barcode",
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
          detail: "Enter a synthetic barcode identifier before sending.",
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
        context: "Endast utveckling · Streckkod",
        title: "Simulerad streckkodsskanning",
        boundary:
          "Detta registrerar endast en rå observation. Ingen operatör är inloggad och det registrerar inte mottagning, sortering, packning, utleverans eller någon annan tvätteriåtgärd.",
        identifierLabel: "Syntetiskt streckkods-ID",
        saving: "Sparar till lokal gateway…",
        retrySame: "Försök med samma skanning igen",
        send: "Skicka simulerad streckkod",
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
          detail: "Ange ett syntetiskt streckkods-ID innan du skickar.",
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
      scopeNote:
        "Den här utvecklingsapplikationen verifierar källregistrering och lagring av råa skanningar. Operatörsinloggning och produktionsflöden är inte implementerade.",
    },
  },
} as const;

export type SupportedLanguage = keyof typeof resources;
