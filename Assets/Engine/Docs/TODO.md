## Need to check if a chunk, partition or render is still relevant before processing it.
This is to avoid doing work on chunks that have been requested at some point, but are no longer needed. 
This can happen when the player moves around and requests different chunks, partitions or renders, and some of 
them become obsolete before they are processed.
