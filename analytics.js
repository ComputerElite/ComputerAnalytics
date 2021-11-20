const aanalyticsVersion = "1.0"
const aanalyticsHosts = [{0}]
const token = "{1}"
var aanalytic = {
    analyticsVersion: aanalyticsVersion,
    fullUri: window.location.href,
    sideOpen: Math.floor(Date.now() / 1000),
    sideClose: 0,
    referrer: document.referrer,
    token: token
}


var asent = false

window.onbeforeunload = aSendAnalytics
window.onunload = aSendAnalytics
window.onpopstate = aSendAnalytics

function aSendAnalytics() {
    if (asent) return
    asent = true
    aanalytic.sideClose = Math.floor(Date.now() / 1000)
    let headers = {
        type: 'application/json'
    };
    let blob = new Blob([JSON.stringify(aanalytic)], headers);

    navigator.sendBeacon(aanalyticsHosts.includes(window.location.origin) ? "/analytics" : aanalyticsHosts[0] + "analytics", blob)
}