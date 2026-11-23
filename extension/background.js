// Clicking the toolbar icon opens the Collaborate-Bench side panel next to the current tab.
chrome.runtime.onInstalled.addListener(() => {
  chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => { /* older Chrome: the panel opens from the side panel menu */ });
});
