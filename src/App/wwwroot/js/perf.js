// Paint timing helper shared by the main UI (index.html) and the tooltip overlay
// (overlay.html).
//
// Blazor's OnAfterRender fires once the DOM diff has been applied, which is NOT
// the same as the user seeing the result: layout, raster and compositing still
// have to happen, and that is exactly where a slow scan can hide. Waiting for two
// animation frames resolves after the frame containing the change has actually
// been submitted, so the reported number is time-to-visible.
window.RatScannerPerf = window.RatScannerPerf || {
	awaitFrame: function () {
		return new Promise(function (resolve) {
			requestAnimationFrame(function () {
				requestAnimationFrame(function () {
					resolve(performance.now());
				});
			});
		});
	},
};
