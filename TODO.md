## Phase 1
### Playback
- [x] auto play next episode in show
    - [x] returns to the show page, not the previous series... should return to the parent page of the current media item?
    - [x] warning/cancel button before playing next episode
- [x] after video finishes with nothing next, go back to previous page
### Programme Details
- [x] fix nested programme details navigation
### Search
- [x] timeout on 3 digit search

Phase 2
- [x] subtitle hint
- [x] show subtitle in player

## Playback
- [x] loading screen
- [x] press space to play/pause the stream.
- [x] the tvheadend stream includes multiple audio streams including audio descriptions. How can we choose the standard english audio?
- [ ] This Video options: fav, subtitles 
- [ ] When streaming on raspberry pi 5, the image is tearing. 
- [ ] after video ends, continue to next season.
- [x] subtitle hint

## Search
- [x] don't search episodes
- [ ] search debugging - why does it sometimes hang?
- [ ] search tvdb index and pull video stream from suggested data
- [ ] group shows and movies in their own rows in search
- [x] search suggestion history
- [x] When searching right after the application opens, search box can be closed when the initial video starts
- [x] show episode length
- [x] cache TV channels inside tvheadend

## Runtime
- [ ] Disable screensaver, energy saver
- [x] auto-update
- [x] auto-restart
- [x] 69 errors
- [x] general navigation weirdness
- [x] built in screensaver
- [ ] built in power saving

## Programme Details
- [x] show series name etc in episode view
- [x] show episode length

## New Features
- [ ] faves - hold to add to faves, faves page
- [ ] embedded encrypted videos

## History
- [x] When i press Down to load the history page, the keyboard input still goes to the video player. 
- [x] Up from the history view should hide history, not open search
- [x] crash when saving infinite as a progress.