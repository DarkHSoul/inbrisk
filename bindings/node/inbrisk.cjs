"use strict";

// Optional FFI wrapper. Install `koffi` (or `ffi-napi`) next to the caller.
// The DLL is the C ABI; this file does not automate Windows itself.
//
//   const inbrisk = require("./bindings/node/inbrisk.cjs");
//   console.log(inbrisk.status());
//   console.log(inbrisk.run({ steps: [{ action: "sleep", ms: 10 }] }, true));

const fs = require("fs");
const path = require("path");

function dllPath() {
  if (process.env.INBRISK_FFI) return process.env.INBRISK_FFI;
  const root = path.resolve(__dirname, "..", "..");
  for (const name of ["release", "debug"]) {
    const candidate = path.join(root, "target", name, "inbrisk_ffi.dll");
    if (fs.existsSync(candidate)) return candidate;
  }
  throw new Error("inbrisk_ffi.dll was not found. Build crates/inbrisk-ffi or set INBRISK_FFI.");
}

function load() {
  const file = dllPath();
  try {
    const koffi = require("koffi");
    const lib = koffi.load(file);
    return {
      status: lib.func("intptr_t inbrisk_status(uint8_t *buf, size_t cap)"),
      run: lib.func("intptr_t inbrisk_run_json(const char *plan_json, int dry_run, uint8_t *buf, size_t cap)"),
    };
  } catch (koffiError) {
    try {
      const ffi = require("ffi-napi");
      return ffi.Library(file, {
        inbrisk_status: ["int64", ["pointer", "size_t"]],
        inbrisk_run_json: ["int64", ["string", "int", "pointer", "size_t"]],
      });
    } catch (ffiError) {
      throw new Error(
        "Install the optional package koffi or ffi-napi to load inbrisk_ffi.dll. " +
          koffiError.message
      );
    }
  }
}

let lib = null;

function api() {
  if (!lib) lib = load();
  return lib;
}

function read(invoke) {
  const buf = Buffer.alloc(1024 * 1024);
  const n = Number(invoke(buf));
  if (!Number.isFinite(n) || n < 0) {
    throw new Error("inbrisk_ffi call failed");
  }
  return JSON.parse(buf.subarray(0, n).toString("utf8"));
}

function status() {
  const bound = api();
  if (typeof bound.status === "function" && bound.inbrisk_status === undefined) {
    return read((buf) => bound.status(buf, buf.length));
  }
  return read((buf) => bound.inbrisk_status(buf, buf.length));
}

function run(plan, dryRun = false) {
  const bound = api();
  const json = JSON.stringify(plan);
  if (typeof bound.run === "function" && bound.inbrisk_run_json === undefined) {
    return read((buf) => bound.run(json, dryRun ? 1 : 0, buf, buf.length));
  }
  return read((buf) => bound.inbrisk_run_json(json, dryRun ? 1 : 0, buf, buf.length));
}

module.exports = { status, run, dllPath };
