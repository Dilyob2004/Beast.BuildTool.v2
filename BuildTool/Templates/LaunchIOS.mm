// iOS entry point: a UIKit application with one screen (built as {{PROJECT_NAME}}.app).
// Beast compiles this file only for iOS (it is in an "IOS" directory) with ARC enabled.
// Scene lifecycle: since iOS 27, UIKit stops apps that create their window in the app delegate.
// Info.plist declares UIApplicationSceneManifest; the app delegate names the scene delegate,
// which creates the window.
#import <UIKit/UIKit.h>
#include <cstdio>
#include <cstdlib>

@interface BeastSceneDelegate : UIResponder <UIWindowSceneDelegate>
@property (strong, nonatomic) UIWindow* window;
@end

@implementation BeastSceneDelegate
- (void)scene:(UIScene*)scene willConnectToSession:(UISceneSession*)session options:(UISceneConnectionOptions*)options
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
    self.window = [[UIWindow alloc] initWithWindowScene:(UIWindowScene*)scene];
    self.window.rootViewController = controller;
    [self.window makeKeyAndVisible];

    std::printf("Hello from {{PROJECT_NAME}}!\n");
    std::fflush(stdout);
    // Automated runs set this (simctl: SIMCTL_CHILD_BEAST_QUIT_AFTER_LAUNCH=1) to check that the app starts.
    // The short delay lets simctl's console relay the output before the process ends.
    if (std::getenv("BEAST_QUIT_AFTER_LAUNCH"))
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), dispatch_get_main_queue(), ^{ std::exit(0); });
}
@end

@interface BeastAppDelegate : UIResponder <UIApplicationDelegate>
@end

@implementation BeastAppDelegate
- (BOOL)application:(UIApplication*)application didFinishLaunchingWithOptions:(NSDictionary*)options
{
    return YES;
}

- (UISceneConfiguration*)application:(UIApplication*)application configurationForConnectingSceneSession:(UISceneSession*)session
                             options:(UISceneConnectionOptions*)options
{
    UISceneConfiguration* configuration = [[UISceneConfiguration alloc] initWithName:@"Default" sessionRole:session.role];
    configuration.delegateClass = [BeastSceneDelegate class];
    return configuration;
}
@end

int main(int argc, char* argv[])
{
    @autoreleasepool
    {
        return UIApplicationMain(argc, argv, nil, NSStringFromClass([BeastAppDelegate class]));
    }
}
