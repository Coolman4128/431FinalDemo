# How to Run

This program has the following requirements:
1. .NET (I am using .NET 10)
2. An LLM provider. You may use a local hosted server (the program looks for an OpenAI compatable API at localhost:1234, I tested with LMStudio) or use OpenAIs API with a key.

To run the program, launch the BrowserTesting.Desktop project to boot up the AvaloniaUI GUI. Navigate to the settings page and select the provider you wish to use and select the model. This program requires heavy use of tool calls, and extensive HTML knowledge in the model so I have found ChatGPT 5.4 full sized to work the best, but local hosted models (I used Qwen 4.6 27B) also can work, they just take a lot longer (on my setup).

You should be able to describe the test you want to run in plain text to the model and it will use its tools to call the appropraite Selenium commands to run the test. The program also has a goal system that allows the AI to pass or fail test cases based on how the website behaves. 