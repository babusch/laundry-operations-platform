import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const read = path => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const schema = read("../requests/submit-scan.v1.schema.json");
const eventSchema = read("../events/scan-observed.v1.schema.json");
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validate = ajv.compile(schema);
const validateEvent = ajv.compile(eventSchema);

for (const technology of ["barcode", "rfid"]) {
  test(`${technology} submission and gateway-enriched event validate`, () => {
    const submission = read(`../examples/submit-scan.v1.${technology}.json`);
    assert.equal(validate(submission), true);
    assert.equal(validateEvent(submission), false);
    const accepted = { ...submission, gatewayAcceptedAtUtc: "2026-09-07T12:00:00.123456Z" };
    assert.equal(validateEvent(accepted), true);
    assert.equal(validate(accepted), false);
  });
}

test("submission fields stay aligned with the accepted-event contract", () => {
  const properties = structuredClone(eventSchema.properties);
  delete properties.gatewayAcceptedAtUtc;
  assert.deepEqual(schema.properties, properties);
  assert.deepEqual(schema.$defs, eventSchema.$defs);
  assert.deepEqual(schema.required, eventSchema.required.filter(x => x !== "gatewayAcceptedAtUtc"));
});

test("all required submission fields are enforced", () => {
  for (const field of schema.required) {
    const submission = read("../examples/submit-scan.v1.barcode.json");
    delete submission[field];
    assert.equal(validate(submission), false, field);
  }
});
