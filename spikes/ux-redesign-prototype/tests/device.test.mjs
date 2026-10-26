import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isLowPower } from '../js/device.js';

const x86 = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';

test('the Pi 5 is low power even when Chromium reports an x86_64 user agent', () => {
  assert.equal(isLowPower({ ua: x86, renderer: 'V3D 7.1.7.0', search: '' }), true);
});
test('software rendering is low power', () => {
  assert.equal(isLowPower({ ua: x86, renderer: 'llvmpipe (LLVM 17.0.6, 128 bits)', search: '' }), true);
  assert.equal(isLowPower({ ua: x86, renderer: 'ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero)), SwiftShader driver)', search: '' }), true);
});
test('an ARM user agent is low power', () => {
  assert.equal(isLowPower({ ua: 'Mozilla/5.0 (X11; Linux aarch64)', renderer: '', search: '' }), true);
});
test('a desktop GPU is not, and ?lite / ?full override', () => {
  assert.equal(isLowPower({ ua: x86, renderer: 'ANGLE (AMD, Radeon RX 7800 XT, OpenGL 4.6)', search: '' }), false);
  assert.equal(isLowPower({ ua: x86, renderer: 'ANGLE (AMD, Radeon RX 7800 XT)', search: '?lite' }), true);
  assert.equal(isLowPower({ ua: x86, renderer: 'V3D 7.1', search: '?full' }), false);
});
