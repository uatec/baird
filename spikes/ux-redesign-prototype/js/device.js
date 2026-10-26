// Lite mode for weak GPUs (the Pi 5's V3D, or software rendering). Chromium's reduced user agent says "x86_64" on
// every Linux and navigator.userAgentData needs https, so the WebGL renderer is the reliable signal.
export function isLowPower({ ua, renderer, search }) {
  const q = new URLSearchParams(search);
  if (q.has('full')) return false;
  if (q.has('lite')) return true;
  return /aarch64|armv|Linux arm/i.test(ua) || /V3D|VC4|Broadcom|Mali|llvmpipe|SwiftShader|softpipe/i.test(renderer);
}

export function webglRenderer() {
  try {
    const gl = document.createElement('canvas').getContext('webgl');
    const ext = gl?.getExtension('WEBGL_debug_renderer_info');
    return (ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl?.getParameter(gl.RENDERER)) ?? '';
  } catch { return ''; }
}
