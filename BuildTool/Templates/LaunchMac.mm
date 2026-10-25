// macOS entry point: an AppKit application with one window (built as {{PROJECT_NAME}}.app).
// Beast compiles this file only for macOS (it is in a "Mac" directory) with ARC enabled.
#import <Cocoa/Cocoa.h>
#include <cstdio>
#include <cstdlib>

@interface BeastAppDelegate : NSObject <NSApplicationDelegate>
@property (strong) NSWindow* window;
@end

@implementation BeastAppDelegate
- (void)applicationDidFinishLaunching:(NSNotification*)notification
{
    NSWindowStyleMask style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable;
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 640, 400) styleMask:style backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"{{PROJECT_NAME}}";
    NSTextField* label = [NSTextField labelWithString:@"Hello from {{PROJECT_NAME}}!"];
    label.font = [NSFont systemFontOfSize:24];
    label.translatesAutoresizingMaskIntoConstraints = NO;
    NSView* content = self.window.contentView;
    [content addSubview:label];
    [NSLayoutConstraint activateConstraints:@[
        [label.centerXAnchor constraintEqualToAnchor:content.centerXAnchor],
        [label.centerYAnchor constraintEqualToAnchor:content.centerYAnchor],
    ]];
    [self.window center];
    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];

    std::printf("Hello from {{PROJECT_NAME}}!\n");
    std::fflush(stdout);
    // Automated runs (CI) set this to check that the application starts.
    if (std::getenv("BEAST_QUIT_AFTER_LAUNCH")) [NSApp terminate:nil];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication*)application
{
    return YES;
}
@end

int main(int argc, const char* argv[])
{
    @autoreleasepool
    {
        NSApplication* application = [NSApplication sharedApplication];
        [application setActivationPolicy:NSApplicationActivationPolicyRegular];
        BeastAppDelegate* delegate = [BeastAppDelegate new];
        application.delegate = delegate;
        [application run];
    }
    return 0;
}
