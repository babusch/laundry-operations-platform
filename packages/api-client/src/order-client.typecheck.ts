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
