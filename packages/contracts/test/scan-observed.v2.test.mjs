import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const read = path => JSON.parse(readFileSync(new URL(path, import.meta.url), "utf8"));
const schema = read("../events/scan-observed.v2.schema.json");
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validate = ajv.compile(schema);

for (const technology of ["barcode", "rfid"]) {
  test(`the ${technology} example satisfies scan-observed v2`, () => {
    const event = read(`../examples/scan-observed.v2.${technology}.json`);
    assert.equal(validate(event), true, ajv.errorsText(validate.errors));
  });
}

test("trusted source attribution is required in v2", () => {
  const event = read("../examples/scan-observed.v2.barcode.json");
  delete event.sourceId;
  assert.equal(validate(event), false);
});

test("physical device and operator claims are not part of a raw v2 observation", () => {
  for (const field of ["deviceId", "operatorId"]) {
    const event = read("../examples/scan-observed.v2.barcode.json");
    event[field] = "99999999-9999-4999-8999-999999999999";
    assert.equal(validate(event), false, field);
  }
});

test("v2 rejects unsupported identifier technology and non-UTC timestamps", () => {
  const unsupported = read("../examples/scan-observed.v2.barcode.json");
  unsupported.identifier.technology = "qr-code";
  assert.equal(validate(unsupported), false);

  const nonUtc = read("../examples/scan-observed.v2.barcode.json");
  nonUtc.observedAtUtc = "2026-09-20T14:00:00.000+02:00";
  assert.equal(validate(nonUtc), false);
});
