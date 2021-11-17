const analyticsVersion = "1.0"
const analyticsHosts = [{0}]
var analytic = {
    analyticsVersion: analyticsVersion,
    fullUri: window.location.href,
    sideOpen: Math.floor(Date.now() / 1000),
    sideClose: 0,
    referrer: document.referrer
}


var sent = false

window.onbeforeunload = SendAnalytics
window.onunload = SendAnalytics
window.onpopstate = SendAnalytics

function SendAnalytics() {
    if (sent) return
    sent = true
    analytic.sideClose = Math.floor(Date.now() / 1000)
    let headers = {
        type: 'application/json'
    };
    let blob = new Blob([JSON.stringify(analytic)], headers);

    navigator.sendBeacon(analyticsHosts.includes(window.location.origin) ? "/analytics" : analyticsHosts[0] + "analytics", blob)
}