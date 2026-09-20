import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const read = path => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const schema = read("../requests/submit-scan.v2.schema.json");
const eventSchema = read("../events/scan-observed.v2.schema.json");
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validate = ajv.compile(schema);
const validateEvent = ajv.compile(eventSchema);

for (const technology of ["barcode", "rfid"]) {
  test(`${technology} v2 submission and gateway-enriched event validate`, () => {
    const submission = read(`../examples/submit-scan.v2.${technology}.json`);
    const accepted = read(`../examples/scan-observed.v2.${technology}.json`);
    assert.equal(validate(submission), true, ajv.errorsText(validate.errors));
    assert.equal(validateEvent(submission), false);
    assert.equal(validateEvent(accepted), true, ajv.errorsText(validateEvent.errors));
    assert.equal(validate(accepted), false);
    for (const field of Object.keys(submission)) assert.deepEqual(accepted[field], submission[field]);
  });
}

test("v2 submission contains no caller-asserted authority or device identity", () => {
  for (const field of ["tenantId", "plantId", "stationId", "sourceId", "deviceId", "operatorId"]) {
    const submission = read("../examples/submit-scan.v2.barcode.json");
    submission[field] = "99999999-9999-4999-8999-999999999999";
    assert.equal(validate(submission), false, field);
  }
});

test("v2 request fields are an exact subset of the accepted event", () => {
  for (const [name, definition] of Object.entries(schema.properties))
    assert.deepEqual(eventSchema.properties[name], definition, name);
  assert.deepEqual(schema.$defs, eventSchema.$defs);
  assert.deepEqual(
    eventSchema.required.filter(field => schema.required.includes(field)),
    schema.required,
  );
  assert.deepEqual(
    eventSchema.required.filter(field => !schema.required.includes(field)),
    ["tenantId", "plantId", "stationId", "sourceId", "gatewayAcceptedAtUtc"],
  );
});

test("all required v2 submission fields are enforced", () => {
  for (const field of schema.required) {
    const submission = read("../examples/submit-scan.v2.barcode.json");
    delete submission[field];
    assert.equal(validate(submission), false, field);
  }
});
