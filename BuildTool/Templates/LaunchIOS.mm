// iOS entry point: a UIKit application with one screen (built as {{PROJECT_NAME}}.app).
// Beast compiles this file only for iOS (it is in an "IOS" directory) with ARC enabled.
#import <UIKit/UIKit.h>
#include <cstdio>
#include <cstdlib>

@interface BeastAppDelegate : UIResponder <UIApplicationDelegate>
@property (strong, nonatomic) UIWindow* window;
@end

@implementation BeastAppDelegate
- (BOOL)application:(UIApplication*)application didFinishLaunchingWithOptions:(NSDictionary*)options
{
    UIViewController* controller = [UIViewController new];
    controller.view.backgroundColor = UIColor.systemBackgroundColor;
    UILabel* label = [UILabel new];
    label.text = @"Hello from {{PROJECT_NAME}}!";
    label.font = [UIFont systemFontOfSize:24];
    label.translatesAutoresizingMaskIntoConstraints = NO;
    [controller.view addSubview:label];
    [NSLayoutConstraint activateConstraints:@[
        [label.centerXAnchor constraintEqualToAnchor:controller.view.centerXAnchor],
        [label.centerYAnchor constraintEqualToAnchor:controller.view.centerYAnchor],
    ]];
    self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = controller;
    [self.window makeKeyAndVisible];

    std::printf("Hello from {{PROJECT_NAME}}!\n");
    std::fflush(stdout);
    // Automated runs set this (simctl: SIMCTL_CHILD_BEAST_QUIT_AFTER_LAUNCH=1) to check that the app starts.
    if (std::getenv("BEAST_QUIT_AFTER_LAUNCH")) std::exit(0);
    return YES;
}
@end

int main(int argc, char* argv[])
{
    @autoreleasepool
    {
        return UIApplicationMain(argc, argv, nil, NSStringFromClass([BeastAppDelegate class]));
    }
}
