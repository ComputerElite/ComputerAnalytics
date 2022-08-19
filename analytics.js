const analyticsVersion = "1.0"
const analyticsHosts = [{0}]
const token = "{1}"
var analytic = {
    analyticsVersion: analyticsVersion,
    fullUri: window.location.href.split('?')[0],
    sideOpen: Math.floor(Date.now() / 1000),
    sideClose: 0,
    referrer: document.referrer,
    token: token,
    screenWidth: window.screen.width,
    screenHeight: window.screen.height
}

var CAid = GetCookie("CAid")
if(!CAid) {
    fetch(analyticsHosts.includes(window.location.origin) ? "/randomtoken" : analyticsHosts[0] + "randomtoken").then(res => {
        res.text().then(res => {
            CAid = res
            SetCookie("CAid", CAid, 90)
            analytic.remote = CAid
        })
    })
} else {
    analytic.remote = CAid
}

var sent = false

window.onbeforeunload = SendAnalytics
window.pagehide = SendAnalytics
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

function GetCookie(cookieName) {
    var name = cookieName + "=";
    var ca = document.cookie.split(';');
    for (var i = 0; i < ca.length; i++) {
        var c = ca[i];
        while (c.charAt(0) == ' ') {
            c = c.substring(1);
        }
        if (c.indexOf(name) == 0) {
            return c.substring(name.length, c.length);
        }
    }
    return "";
}

function SetCookie(name, value, expiration) {
    var d = new Date();
    d.setTime(d.getTime() + (expiration * 24 * 60 * 60 * 1000));
    var expires = "expires=" + d.toUTCString();
    document.cookie = name + "=" + value + ";" + expires + ";path=/";
}