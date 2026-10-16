import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const read = path => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const schema = read("../projections/order-plan.v1.schema.json");
const validate = ajv.compile(schema);
const example = () => read("../examples/order-plan.v1.visible.json");

for (const state of ["visible", "hidden"]) {
  test(`published order v1 accepts the synthetic ${state} example`, () => {
    assert.equal(validate(read(`../examples/order-plan.v1.${state}.json`)), true, ajv.errorsText(validate.errors));
  });
}

test("hidden visibility rejects note text rather than relying on UI concealment", () => {
  for (const notes of ["Synthetic management-only text", "", " "]) {
    assert.equal(validate({ ...example(), notesVisibleToOperators: false, notes }), false);
  }
  assert.equal(validate({ ...example(), notes: null }), true); // Visible but no note entered.
});

test("publication requires every field including explicit null for cleared notes", () => {
  for (const field of schema.required) {
    const order = example();
    delete order[field];
    assert.equal(validate(order), false, field);
  }
  for (const field of ["id", "code", "displayName"]) {
    const order = example();
    delete order.customer[field];
    assert.equal(validate(order), false, `customer.${field}`);
  }
});

test("management references, raw identities and unknown fields cannot enter the projection", () => {
  for (const field of ["customerReference", "actorSubject", "operatorId", "deviceId", "audit", "internalNotes", "unexpected"]) {
    assert.equal(validate({ ...example(), [field]: "synthetic" }), false, field);
  }
  const order = example();
  order.customer.email = "synthetic@example.invalid";
  assert.equal(validate(order), false);
});

test("publication identities must be non-empty UUIDs", () => {
  for (const invalid of ["", "not-an-id", "00000000-0000-0000-0000-000000000000"]) {
    for (const field of ["tenantId", "plantId", "orderId"]) {
      assert.equal(validate({ ...example(), [field]: invalid }), false, field);
    }
    const order = example();
    order.customer.id = invalid;
    assert.equal(validate(order), false);
  }
});

test("publication revision and schema version have strict independent representations", () => {
  for (const version of [0, -1, 1.5, "2", null, 9007199254740992]) {
    assert.equal(validate({ ...example(), version }), false);
  }
  for (const schemaVersion of [2, "1", null]) {
    assert.equal(validate({ ...example(), schemaVersion }), false);
  }
  assert.equal(validate({ ...example(), version: 9007199254740991 }), true);
});

test("unsupported workflow states and non-boolean visibility are rejected", () => {
  for (const status of ["receiving", "completed", "", null]) {
    assert.equal(validate({ ...example(), status }), false);
  }
  for (const notesVisibleToOperators of ["false", 0, null]) {
    assert.equal(validate({ ...example(), notesVisibleToOperators }), false);
  }
});

test("planned dates are calendar dates, not timestamps or invalid dates", () => {
  for (const field of ["plannedReceivingDate", "plannedDeliveryDate"]) {
    for (const invalid of ["2030-02-30", "2030-01-02T00:00:00Z", "02/01/2030", null]) {
      assert.equal(validate({ ...example(), [field]: invalid }), false, field);
    }
  }
});

test("published text follows existing customer and note limits", () => {
  for (const [field, limit] of [["code", 50], ["displayName", 200]]) {
    const order = example();
    order.customer[field] = "x".repeat(limit);
    assert.equal(validate(order), true);
    for (const invalid of ["x".repeat(limit + 1), "", " "]) {
      order.customer[field] = invalid;
      assert.equal(validate(order), false, field);
    }
  }
  assert.equal(validate({ ...example(), notes: "x".repeat(2000) }), true);
  for (const notes of ["x".repeat(2001), "", " "]) {
    assert.equal(validate({ ...example(), notes }), false);
  }
  assert.equal(validate({ ...example(), orderNumber: " " }), false);
});
