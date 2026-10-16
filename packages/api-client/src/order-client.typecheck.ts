import type { components, operations } from "./generated/orders-api";

// Compile-only contract checks: generated replacements must be usable and must
// require explicit optional-text clearing rather than silently omitting fields.
type Assert<T extends true> = T;
type Plan = components["schemas"]["UpdateOrderPlan"];
type NullTextIsAccepted = Assert<{
  customerId: string;
  plannedReceivingDate: string;
  plannedDeliveryDate: string;
  customerReference: null;
  notes: null;
} extends Plan ? true : false>;
type NotesAreRequired = Assert<Omit<Plan, "notes"> extends Plan ? false : true>;
type ReferenceIsRequired = Assert<
  Omit<Plan, "customerReference"> extends Plan ? false : true
>;
type Headers = operations["updatePlannedOrder"]["parameters"]["header"];
type VersionIsRequired = Assert<
  Omit<Headers, "If-Match"> extends Headers ? false : true
>;

type History = components["schemas"]["OrderHistoryEntry"];
type MissingHistoryNameIsRepresentable = Assert<
  null extends History["actorDisplayName"] ? true : false
>;
type NoRawHistoryIdentity = Assert<
  "actorSubject" extends keyof History ? false : true
>;
type HistoryChangesPermitClearing = Assert<
  null extends components["schemas"]["OrderPlanChange"]["after"] ? true : false
>;

type VisibilityCanBeOmitted = Assert<
  Omit<Plan, "notesVisibleToOperators"> extends Plan ? true : false
>;
type VisibilityRejectsNull = Assert<
  null extends Plan["notesVisibleToOperators"] ? false : true
>;
type HistoricalVisibilityCanBeUnknown = Assert<
  null extends components["schemas"]["OrderHistoryPlan"]["notesVisibleToOperators"] ? true : false
>;
type LegacyReceiptCanOmitVisibility = Assert<
  Omit<components["schemas"]["Order"], "notesVisibleToOperators"> extends components["schemas"]["Order"] ? true : false
>;
type DefaultPreconditionRequired = Assert<
  Omit<operations["updateOrderNotesDefault"]["parameters"]["header"], "If-Match"> extends operations["updateOrderNotesDefault"]["parameters"]["header"] ? false : true
>;
