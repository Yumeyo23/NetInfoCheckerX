/*
	LibreSpeed - Worker (修改版)
	- 尊重显式兼容性设置，并对并发/时长参数做边界校验
	- 回传配置与已创建的并发流数量，便于界面核对档位是否生效
*/

// data reported to main thread
let testState = -1;
let dlStatus = "";
let ulStatus = "";
let pingStatus = "";
let jitterStatus = "";
let clientIp = "";
let dlProgress = 0;
let ulProgress = 0;
let pingProgress = 0;
let testId = null;

let log = "";
function tlog(s) {
	if (settings.telemetry_level >= 2) {
		log += Date.now() + ": " + s + "\n";
	}
}
function tverb(s) {
	if (settings.telemetry_level >= 3) {
		log += Date.now() + ": " + s + "\n";
	}
}
function twarn(s) {
	if (settings.telemetry_level >= 2) {
		log += Date.now() + " WARN: " + s + "\n";
	}
	console.warn(s);
}

let settings = {
	mpot: false,
	test_order: "IP_D_U",
	time_ul_max: 15,
	time_dl_max: 15,
	time_auto: true,
	time_ulGraceTime: 3,
	time_dlGraceTime: 1.5,
	count_ping: 10,
	url_dl: "backend/garbage.php",
	url_ul: "backend/empty.php",
	url_ping: "backend/empty.php",
	url_getIp: "backend/getIP.php",
	getIp_ispInfo: true,
	getIp_ispInfo_distance: "km",
	xhr_dlMultistream: 6,
	xhr_ulMultistream: 3,
	xhr_multistreamDelay: 300,
	xhr_ignoreErrors: 1,
	xhr_dlUseBlob: false,
	xhr_ul_blob_megabytes: 20,
	xhr_ulStrictConcurrency: false,
	garbagePhp_chunkSize: 2,
	enable_quirks: true,
	ping_allowPerformanceApi: true,
	overheadCompensationFactor: 1.06,
	useMebibits: false,
	telemetry_level: 0,
	url_telemetry: "results/telemetry.php",
	telemetry_extra: "",
	forceIE11Workaround: false
};

let xhr = null;
let interval = null;
let test_pointer = 0;
let streamMetrics = {
	dlConfigured: 0,
	ulConfigured: 0,
	dlStarted: 0,
	ulStarted: 0,
	dlRequestsInFlight: 0,
	dlPeakRequestsInFlight: 0,
	ulRequestsInFlight: 0,
	ulPeakRequestsInFlight: 0
};

function url_sep(url) {
	return url.match(/\?/) ? "&" : "?";
}

function clampInteger(value, min, max, fallback) {
	const parsed = Number(value);
	if (!isFinite(parsed)) return fallback;
	return Math.min(max, Math.max(min, Math.floor(parsed)));
}

this.addEventListener("message", function(e) {
	const params = e.data.split(" ");
	if (params[0] === "status") {
		postMessage(JSON.stringify({
			testState: testState,
			dlStatus: dlStatus,
			ulStatus: ulStatus,
			pingStatus: pingStatus,
			clientIp: clientIp,
			jitterStatus: jitterStatus,
			dlProgress: dlProgress,
			ulProgress: ulProgress,
			pingProgress: pingProgress,
			testId: testId,
			dlStreamsConfigured: streamMetrics.dlConfigured,
			ulStreamsConfigured: streamMetrics.ulConfigured,
			dlStreamsStarted: streamMetrics.dlStarted,
			ulStreamsStarted: streamMetrics.ulStarted,
			dlRequestsInFlight: streamMetrics.dlRequestsInFlight,
			dlPeakRequestsInFlight: streamMetrics.dlPeakRequestsInFlight,
			ulRequestsInFlight: streamMetrics.ulRequestsInFlight,
			ulPeakRequestsInFlight: streamMetrics.ulPeakRequestsInFlight,
			multistreamDelay: settings.xhr_multistreamDelay
		}));
	}
	if (params[0] === "start" && testState === -1) {
		testState = 0;
		try {
			let s = {};
			try {
				const ss = e.data.substring(5);
				if (ss) s = JSON.parse(ss);
			} catch (e) {
				twarn("Error parsing custom settings JSON. Please check your syntax");
			}
			// 先记录用户是否显式传入了 forceIE11Workaround
			let userForceIE11 = (typeof s.forceIE11Workaround !== "undefined");
			// 保存标记到 settings，供后续 testStream 使用
			settings._forceIE11Explicit = userForceIE11;
			for (let key in s) {
				if (typeof settings[key] !== "undefined") settings[key] = s[key];
				else twarn("Unknown setting ignored: " + key);
			}
			const ua = navigator.userAgent;
			if (settings.enable_quirks || (typeof s.enable_quirks !== "undefined" && s.enable_quirks)) {
				if (/Firefox.(\d+\.\d+)/i.test(ua)) {
					if (typeof s.ping_allowPerformanceApi === "undefined") {
						settings.ping_allowPerformanceApi = false;
					}
				}
				if (/Edge.(\d+\.\d+)/i.test(ua)) {
					if (typeof s.xhr_dlMultistream === "undefined") {
						settings.xhr_dlMultistream = 3;
					}
				}
				if (/Chrome.(\d+)/i.test(ua) && !!self.fetch) {
					if (typeof s.xhr_dlMultistream === "undefined") {
						settings.xhr_dlMultistream = 5;
					}
				}
			}
			// 【关键修改】只有用户没有显式设置 forceIE11Workaround 时，才自动启用回退
			if (!userForceIE11) {
				if (/Edge.(\d+\.\d+)/i.test(ua)) {
					settings.forceIE11Workaround = true;
				}
				if (/PlayStation 4.(\d+\.\d+)/i.test(ua)) {
					settings.forceIE11Workaround = true;
				}
				if (/^((?!chrome|android|crios|fxios).)*safari/i.test(ua)) {
					settings.forceIE11Workaround = true;
				}
			} else {
				// 用户已经指定，保持原值，不再覆盖
				tverb("forceIE11Workaround 由用户设置为 " + settings.forceIE11Workaround);
			}
			if (/Chrome.(\d+)/i.test(ua) && /Android|iPhone|iPad|iPod|Windows Phone/i.test(ua)) {
				settings.xhr_ul_blob_megabytes = 4;
			}
			if (typeof s.telemetry_level !== "undefined") settings.telemetry_level = s.telemetry_level === "basic" ? 1 : s.telemetry_level === "full" ? 2 : s.telemetry_level === "debug" ? 3 : 0;
			settings.xhr_dlMultistream = clampInteger(settings.xhr_dlMultistream, 1, 64, 6);
			settings.xhr_ulMultistream = clampInteger(settings.xhr_ulMultistream, 1, 64, 3);
			settings.xhr_multistreamDelay = clampInteger(settings.xhr_multistreamDelay, 0, 2000, 300);
			settings.time_dl_max = clampInteger(settings.time_dl_max, 1, 60, 15);
			settings.time_ul_max = clampInteger(settings.time_ul_max, 1, 60, 15);
			settings.xhr_ul_blob_megabytes = clampInteger(settings.xhr_ul_blob_megabytes, 1, 100, 20);
			settings.garbagePhp_chunkSize = clampInteger(settings.garbagePhp_chunkSize, 1, 4, 2);
			if ([1, 2, 4].indexOf(settings.garbagePhp_chunkSize) < 0) settings.garbagePhp_chunkSize = 2;
			streamMetrics.dlConfigured = settings.xhr_dlMultistream;
			streamMetrics.ulConfigured = settings.xhr_ulMultistream;
			streamMetrics.dlStarted = 0;
			streamMetrics.ulStarted = 0;
			streamMetrics.dlRequestsInFlight = 0;
			streamMetrics.dlPeakRequestsInFlight = 0;
			streamMetrics.ulRequestsInFlight = 0;
			streamMetrics.ulPeakRequestsInFlight = 0;
			settings.test_order = settings.test_order.toUpperCase();
		} catch (e) {
			twarn("Possible error in custom test settings. Exception: " + e);
		}
		tverb(JSON.stringify(settings));
		test_pointer = 0;
		let iRun = false, dRun = false, uRun = false, pRun = false;
		const runNextTest = function() {
			if (testState == 5) return;
			if (test_pointer >= settings.test_order.length) {
				if (settings.telemetry_level > 0)
					sendTelemetry(function(id) {
						testState = 4;
						if (id != null) testId = id;
					});
				else testState = 4;
				return;
			}
			switch (settings.test_order.charAt(test_pointer)) {
				case "I":
					test_pointer++;
					if (iRun) { runNextTest(); return; }
					iRun = true;
					getIp(runNextTest);
					break;
				case "D":
					test_pointer++;
					if (dRun) { runNextTest(); return; }
					dRun = true;
					testState = 1;
					dlTest(runNextTest);
					break;
				case "U":
					test_pointer++;
					if (uRun) { runNextTest(); return; }
					uRun = true;
					testState = 3;
					ulTest(runNextTest);
					break;
				case "P":
					test_pointer++;
					if (pRun) { runNextTest(); return; }
					pRun = true;
					testState = 2;
					pingTest(runNextTest);
					break;
				case "_":
					test_pointer++;
					setTimeout(runNextTest, 1000);
					break;
				default:
					test_pointer++;
			}
		};
		runNextTest();
	}
	if (params[0] === "abort") {
		if (testState >= 4) return;
		tlog("manually aborted");
		clearRequests();
		runNextTest = null;
		if (interval) clearInterval(interval);
		if (settings.telemetry_level > 1) sendTelemetry(function() {});
		testState = 5;
		dlStatus = "";
		ulStatus = "";
		pingStatus = "";
		jitterStatus = "";
		clientIp = "";
		dlProgress = 0;
		ulProgress = 0;
		pingProgress = 0;
	}
});

function clearRequests() {
	tverb("stopping pending XHRs");
	if (xhr) {
		for (let i = 0; i < xhr.length; i++) {
			try { xhr[i].onprogress = null; } catch(e) {}
			try { xhr[i].onload = null; } catch(e) {}
			try { xhr[i].onerror = null; } catch(e) {}
			try { xhr[i].upload.onprogress = null; } catch(e) {}
			try { xhr[i].upload.onload = null; } catch(e) {}
			try { xhr[i].upload.onerror = null; } catch(e) {}
			try { xhr[i].abort(); } catch(e) {}
			try { delete xhr[i]; } catch(e) {}
		}
		xhr = null;
	}
	streamMetrics.dlRequestsInFlight = 0;
	streamMetrics.ulRequestsInFlight = 0;
}

let ipCalled = false;
let ispInfo = "";
function getIp(done) {
	tverb("getIp");
	if (ipCalled) return;
	ipCalled = true;
	let startT = new Date().getTime();
	xhr = new XMLHttpRequest();
	xhr.onload = function() {
		tlog("IP: " + xhr.responseText + ", took " + (new Date().getTime() - startT) + "ms");
		try {
			const data = JSON.parse(xhr.responseText);
			clientIp = data.processedString;
			ispInfo = data.rawIspInfo;
		} catch (e) {
			clientIp = xhr.responseText;
			ispInfo = "";
		}
		done();
	};
	xhr.onerror = function() {
		tlog("getIp failed, took " + (new Date().getTime() - startT) + "ms");
		done();
	};
	xhr.open("GET", settings.url_getIp + url_sep(settings.url_getIp) + (settings.mpot ? "cors=true&" : "") + (settings.getIp_ispInfo ? "isp=true" + (settings.getIp_ispInfo_distance ? "&distance=" + settings.getIp_ispInfo_distance + "&" : "&") : "&") + "r=" + Math.random(), true);
	xhr.send();
}

let dlCalled = false;
function dlTest(done) {
	tverb("dlTest");
	if (dlCalled) return;
	dlCalled = true;
	let totLoaded = 0.0, startT = new Date().getTime(), bonusT = 0, graceTimeDone = false, failed = false;
	let startedStreams = [];
	xhr = [];
	const testStream = function(i, delay) {
		setTimeout(function() {
			if (testState !== 1) return;
			if (!startedStreams[i]) {
				startedStreams[i] = true;
				streamMetrics.dlStarted++;
			}
			tverb("dl test stream started " + i + " " + delay);
			let prevLoaded = 0;
			let x = new XMLHttpRequest();
			xhr[i] = x;
			let requestFinished = false;
			const finishRequest = function() {
				if (requestFinished) return;
				requestFinished = true;
				streamMetrics.dlRequestsInFlight = Math.max(0, streamMetrics.dlRequestsInFlight - 1);
			};
			streamMetrics.dlRequestsInFlight++;
			if (streamMetrics.dlRequestsInFlight > streamMetrics.dlPeakRequestsInFlight) {
				streamMetrics.dlPeakRequestsInFlight = streamMetrics.dlRequestsInFlight;
			}
			xhr[i].onprogress = function(event) {
				tverb("dl stream progress event " + i + " " + event.loaded);
				if (testState !== 1) { try { x.abort(); } catch(e) {} }
				const loadDiff = event.loaded <= 0 ? 0 : event.loaded - prevLoaded;
				if (isNaN(loadDiff) || !isFinite(loadDiff) || loadDiff < 0) return;
				totLoaded += loadDiff;
				prevLoaded = event.loaded;
			};
			xhr[i].onload = function() {
				tverb("dl stream finished " + i);
				finishRequest();
				try { x.abort(); } catch(e) {}
				testStream(i, 0);
			};
			xhr[i].onerror = function() {
				tverb("dl stream failed " + i);
				finishRequest();
				if (settings.xhr_ignoreErrors === 0) failed = true;
				try { x.abort(); } catch(e) {}
				if (xhr[i] === x) delete xhr[i];
				if (settings.xhr_ignoreErrors === 1) testStream(i, 0);
			};
			try {
				if (settings.xhr_dlUseBlob) xhr[i].responseType = "blob";
				else xhr[i].responseType = "arraybuffer";
			} catch(e) {}
			xhr[i].open("GET", settings.url_dl + url_sep(settings.url_dl) + (settings.mpot ? "cors=true&" : "") + "r=" + Math.random() + "&ckSize=" + settings.garbagePhp_chunkSize, true);
			xhr[i].send();
		}, 1 + delay);
	};
	for (let i = 0; i < settings.xhr_dlMultistream; i++) {
		testStream(i, settings.xhr_multistreamDelay * i);
	}
	interval = setInterval(function() {
		tverb("DL: " + dlStatus + (graceTimeDone ? "" : " (in grace time)"));
		const t = new Date().getTime() - startT;
		if (graceTimeDone) dlProgress = (t + bonusT) / (settings.time_dl_max * 1000);
		if (t < 200) return;
		if (!graceTimeDone) {
			if (t > 1000 * settings.time_dlGraceTime) {
				if (totLoaded > 0) {
					startT = new Date().getTime();
					bonusT = 0;
					totLoaded = 0.0;
				}
				graceTimeDone = true;
			}
		} else {
			const speed = totLoaded / (t / 1000.0);
			if (settings.time_auto) {
				const bonus = (5.0 * speed) / 100000;
				bonusT += bonus > 400 ? 400 : bonus;
			}
			dlStatus = ((speed * 8 * settings.overheadCompensationFactor) / (settings.useMebibits ? 1048576 : 1000000)).toFixed(2);
			if ((t + bonusT) / 1000.0 > settings.time_dl_max || failed) {
				if (failed || isNaN(dlStatus)) dlStatus = "Fail";
				clearRequests();
				clearInterval(interval);
				dlProgress = 1;
				tlog("dlTest: " + dlStatus + ", took " + (new Date().getTime() - startT) + "ms");
				done();
			}
		}
	}, 200);
}

let ulCalled = false;
function ulTest(done) {
	tverb("ulTest");
	if (ulCalled) return;
	ulCalled = true;
	let r = new ArrayBuffer(1048576);
	const maxInt = Math.pow(2, 32) - 1;
	try {
		r = new Uint32Array(r);
		for (let i = 0; i < r.length; i++) r[i] = Math.random() * maxInt;
	} catch(e) {}
	let req = [];
	let reqsmall = [];
	for (let i = 0; i < settings.xhr_ul_blob_megabytes; i++) req.push(r);
	req = new Blob(req);
	r = new ArrayBuffer(262144);
	try {
		r = new Uint32Array(r);
		for (let i = 0; i < r.length; i++) r[i] = Math.random() * maxInt;
	} catch(e) {}
	reqsmall.push(r);
	reqsmall = new Blob(reqsmall);
	const testFunction = function() {
		let totLoaded = 0.0, startT = new Date().getTime(), bonusT = 0, graceTimeDone = false, failed = false;
		let startedStreams = [];
		xhr = [];
		const testStream = function(i, delay) {
			setTimeout(function() {
				if (testState !== 3) return;
				if (!startedStreams[i]) {
					startedStreams[i] = true;
					streamMetrics.ulStarted++;
				}
				tverb("ul test stream started " + i + " " + delay);
				let prevLoaded = 0;
				let x = new XMLHttpRequest();
				xhr[i] = x;
				let requestFinished = false;
				let restartRequested = false;
				const finishRequest = function() {
					if (requestFinished) return;
					requestFinished = true;
					streamMetrics.ulRequestsInFlight = Math.max(0, streamMetrics.ulRequestsInFlight - 1);
				};
				const restartStream = function() {
					if (restartRequested) return;
					restartRequested = true;
					if (testState === 3) testStream(i, 0);
				};
				const handleRequestError = function() {
					tverb("ul stream failed " + i);
					finishRequest();
					if (settings.xhr_ignoreErrors === 0) failed = true;
					try { x.abort(); } catch(e) {}
					if (xhr[i] === x) delete xhr[i];
					if (settings.xhr_ignoreErrors === 1) restartStream();
				};
				streamMetrics.ulRequestsInFlight++;
				if (streamMetrics.ulRequestsInFlight > streamMetrics.ulPeakRequestsInFlight) {
					streamMetrics.ulPeakRequestsInFlight = streamMetrics.ulRequestsInFlight;
				}
				let ie11workaround = settings.forceIE11Workaround;
				// 当用户显式设置了 forceIE11Workaround 时，跳过运行时特性检测，
				// 确保上传并发严格受 xhr_ulMultistream 控制，不会被回退模式覆盖
				if (!ie11workaround && !settings._forceIE11Explicit) {
					try {
						xhr[i].upload.onprogress;
					} catch(e) {
						ie11workaround = true;
					}
				}
				if (ie11workaround) {
					x.onload = function() {
						tverb("ul stream progress event (ie11wa)");
						totLoaded += reqsmall.size;
						finishRequest();
						restartStream();
					};
					x.onerror = handleRequestError;
					x.open("POST", settings.url_ul + url_sep(settings.url_ul) + (settings.mpot ? "cors=true&" : "") + "r=" + Math.random(), true);
					try { x.setRequestHeader("Content-Encoding", "identity"); } catch(e) {}
					x.send(reqsmall);
				} else {
					x.upload.onprogress = function(event) {
						tverb("ul stream progress event " + i + " " + event.loaded);
						if (testState !== 3) { try { x.abort(); } catch(e) {} }
						const loadDiff = event.loaded <= 0 ? 0 : event.loaded - prevLoaded;
						if (isNaN(loadDiff) || !isFinite(loadDiff) || loadDiff < 0) return;
						totLoaded += loadDiff;
						prevLoaded = event.loaded;
					};
					x.upload.onload = function() {
						tverb("ul request body sent " + i);
						if (!settings.xhr_ulStrictConcurrency) restartStream();
					};
					x.onload = function() {
						tverb("ul request response received " + i);
						finishRequest();
						if (settings.xhr_ulStrictConcurrency) restartStream();
					};
					x.onerror = handleRequestError;
					x.upload.onerror = handleRequestError;
					x.open("POST", settings.url_ul + url_sep(settings.url_ul) + (settings.mpot ? "cors=true&" : "") + "r=" + Math.random(), true);
					try { x.setRequestHeader("Content-Encoding", "identity"); } catch(e) {}
					x.send(req);
				}
			}, delay);
		};
		for (let i = 0; i < settings.xhr_ulMultistream; i++) {
			testStream(i, settings.xhr_multistreamDelay * i);
		}
		interval = setInterval(function() {
			tverb("UL: " + ulStatus + (graceTimeDone ? "" : " (in grace time)"));
			const t = new Date().getTime() - startT;
			if (graceTimeDone) ulProgress = (t + bonusT) / (settings.time_ul_max * 1000);
			if (t < 200) return;
			if (!graceTimeDone) {
				if (t > 1000 * settings.time_ulGraceTime) {
					if (totLoaded > 0) {
						startT = new Date().getTime();
						bonusT = 0;
						totLoaded = 0.0;
					}
					graceTimeDone = true;
				}
			} else {
				const speed = totLoaded / (t / 1000.0);
				if (settings.time_auto) {
					const bonus = (5.0 * speed) / 100000;
					bonusT += bonus > 400 ? 400 : bonus;
				}
				ulStatus = ((speed * 8 * settings.overheadCompensationFactor) / (settings.useMebibits ? 1048576 : 1000000)).toFixed(2);
				if ((t + bonusT) / 1000.0 > settings.time_ul_max || failed) {
					if (failed || isNaN(ulStatus)) ulStatus = "Fail";
					clearRequests();
					clearInterval(interval);
					ulProgress = 1;
					tlog("ulTest: " + ulStatus + ", took " + (new Date().getTime() - startT) + "ms");
					done();
				}
			}
		}, 200);
	};
	if (settings.mpot) {
		tverb("Sending POST request before performing upload test");
		xhr = [];
		xhr[0] = new XMLHttpRequest();
		xhr[0].onload = xhr[0].onerror = function() {
			tverb("POST request sent, starting upload test");
			testFunction();
		};
		xhr[0].open("POST", settings.url_ul);
		xhr[0].send();
	} else testFunction();
}

let ptCalled = false;
function pingTest(done) {
	tverb("pingTest");
	if (ptCalled) return;
	ptCalled = true;
	const startT = new Date().getTime();
	let prevT = null, ping = 0.0, jitter = 0.0, i = 0, prevInstspd = 0;
	xhr = [];
	const doPing = function() {
		tverb("ping");
		pingProgress = i / settings.count_ping;
		prevT = new Date().getTime();
		xhr[0] = new XMLHttpRequest();
		xhr[0].onload = function() {
			tverb("pong");
			if (i === 0) {
				prevT = new Date().getTime();
			} else {
				let instspd = new Date().getTime() - prevT;
				if (settings.ping_allowPerformanceApi) {
					try {
						let p = performance.getEntries();
						p = p[p.length - 1];
						let d = p.responseStart - p.requestStart;
						if (d <= 0) d = p.duration;
						if (d > 0 && d < instspd) instspd = d;
					} catch(e) {}
				}
				if (instspd < 1) instspd = prevInstspd;
				if (instspd < 1) instspd = 1;
				const instjitter = Math.abs(instspd - prevInstspd);
				if (i === 1) ping = instspd;
				else {
					if (instspd < ping) ping = instspd;
					if (i === 2) jitter = instjitter;
					else jitter = instjitter > jitter ? jitter * 0.3 + instjitter * 0.7 : jitter * 0.8 + instjitter * 0.2;
				}
				prevInstspd = instspd;
			}
			pingStatus = ping.toFixed(2);
			jitterStatus = jitter.toFixed(2);
			i++;
			tverb("ping: " + pingStatus + " jitter: " + jitterStatus);
			if (i < settings.count_ping) doPing();
			else {
				pingProgress = 1;
				tlog("ping: " + pingStatus + " jitter: " + jitterStatus + ", took " + (new Date().getTime() - startT) + "ms");
				done();
			}
		};
		xhr[0].onerror = function() {
			tverb("ping failed");
			if (settings.xhr_ignoreErrors === 0) {
				pingStatus = "Fail";
				jitterStatus = "Fail";
				clearRequests();
				tlog("ping test failed, took " + (new Date().getTime() - startT) + "ms");
				pingProgress = 1;
				done();
			}
			if (settings.xhr_ignoreErrors === 1) doPing();
			if (settings.xhr_ignoreErrors === 2) {
				i++;
				if (i < settings.count_ping) doPing();
				else {
					pingProgress = 1;
					tlog("ping: " + pingStatus + " jitter: " + jitterStatus + ", took " + (new Date().getTime() - startT) + "ms");
					done();
				}
			}
		};
		xhr[0].open("GET", settings.url_ping + url_sep(settings.url_ping) + (settings.mpot ? "cors=true&" : "") + "r=" + Math.random(), true);
		xhr[0].send();
	};
	doPing();
}

function sendTelemetry(done) {
	if (settings.telemetry_level < 1) return;
	xhr = new XMLHttpRequest();
	xhr.onload = function() {
		try {
			const parts = xhr.responseText.split(" ");
			if (parts[0] == "id") {
				try {
					let id = parts[1];
					done(id);
				} catch(e) { done(null); }
			} else done(null);
		} catch(e) { done(null); }
	};
	xhr.onerror = function() {
		console.log("TELEMETRY ERROR " + xhr.status);
		done(null);
	};
	xhr.open("POST", settings.url_telemetry + url_sep(settings.url_telemetry) + (settings.mpot ? "cors=true&" : "") + "r=" + Math.random(), true);
	const telemetryIspInfo = {
		processedString: clientIp,
		rawIspInfo: typeof ispInfo === "object" ? ispInfo : ""
	};
	try {
		const fd = new FormData();
		fd.append("ispinfo", JSON.stringify(telemetryIspInfo));
		fd.append("dl", dlStatus);
		fd.append("ul", ulStatus);
		fd.append("ping", pingStatus);
		fd.append("jitter", jitterStatus);
		fd.append("log", settings.telemetry_level > 1 ? log : "");
		fd.append("extra", settings.telemetry_extra);
		xhr.send(fd);
	} catch(ex) {
		const postData = "extra=" + encodeURIComponent(settings.telemetry_extra) + "&ispinfo=" + encodeURIComponent(JSON.stringify(telemetryIspInfo)) + "&dl=" + encodeURIComponent(dlStatus) + "&ul=" + encodeURIComponent(ulStatus) + "&ping=" + encodeURIComponent(pingStatus) + "&jitter=" + encodeURIComponent(jitterStatus) + "&log=" + encodeURIComponent(settings.telemetry_level > 1 ? log : "");
		xhr.setRequestHeader("Content-Type", "application/x-www-form-urlencoded");
		xhr.send(postData);
	}
}
