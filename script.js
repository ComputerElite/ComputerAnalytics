var loading = document.getElementsByClassName("loading")
while(loading.length != 0) {
    loading.item(0).innerHTML = "<div></div><div></div><div></div><div></div>"
    loading.item(0).className ="lds-ellipsis"
}
