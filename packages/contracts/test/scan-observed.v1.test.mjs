import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

function readJson(relativePath) {
  const path = fileURLToPath(new URL(relativePath, import.meta.url));
  return JSON.parse(readFileSync(path, "utf8"));
}

const schema = readJson("../events/scan-observed.v1.schema.json");
const barcodeExample = readJson("../examples/scan-observed.v1.barcode.json");
const rfidExample = readJson("../examples/scan-observed.v1.rfid.json");

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validate = ajv.compile(schema);

function assertValid(event) {
  assert.equal(
    validate(event),
    true,
    ajv.errorsText(validate.errors, { separator: "\n" }),
  );
}

function assertInvalid(event) {
  assert.equal(validate(event), false);
}

test("the barcode example satisfies scan-observed v1", () => {
  assertValid(barcodeExample);
});

test("the RFID example satisfies scan-observed v1", () => {
  assertValid(rfidExample);
});

test("an event without its id is rejected", () => {
  const event = structuredClone(barcodeExample);
  delete event.eventId;

  assertInvalid(event);
});

test("an unsupported identifier technology is rejected", () => {
  const event = structuredClone(barcodeExample);
  event.identifier.technology = "qr-code";

  assertInvalid(event);
});

test("a non-UTC timestamp is rejected", () => {
  const event = structuredClone(barcodeExample);
  event.observedAtUtc = "2026-09-03T14:00:00.000+02:00";

  assertInvalid(event);
});

test("item details cannot be added to a raw observation", () => {
  const event = structuredClone(barcodeExample);
  event.itemDescription = "Example garment";

  assertInvalid(event);
});
