# How to Run

This program has the following requirements:
1. .NET (I am using .NET 10)
2. An LLM provider. You may use a local hosted server (the program looks for an OpenAI compatable API at localhost:1234, I tested with LMStudio) or use OpenAIs API with a key.

To run the program, launch the BrowserTesting.Desktop project to boot up the AvaloniaUI GUI. Navigate to the settings page and select the provider you wish to use and select the model. This program requires heavy use of tool calls, and extensive HTML knowledge in the model so I have found ChatGPT 5.4 full sized to work the best, but local hosted models (I used Qwen 4.6 27B) also can work, they just take a lot longer (on my setup).

You should be able to describe the test you want to run in plain text to the model and it will use its tools to call the appropraite Selenium commands to run the test. The program also has a goal system that allows the AI to pass or fail test cases based on how the website behaves. 

This is the prompt we used for the demo:
==========================================
I want to make 3 goals:
Logging in.
Adding backpack and Bike Light to Cart to verify cart functionality.
Going to the checlkout page, verifiying the cost and then checking out.

Please run the tests in order, and pass or fail each goal as you perform them.

Follow this procedure
Go to https://www.saucedemo.com/
Log in with account name "standard_user" and password "secret_sauce", if it works then pass the first goal. Otherwise fail all 3 goals.
Add the Backpack and Bike Light to the cart, then verify that 2 items are in the cart, if so then pass the second goal, otherwise fail the second and third goal.
Go to the checkout page, verify the cost, then go to checkout. If checkout succedes then pass the third goal, otherwise fail the third goal.
For checkout use a first and last name of "test" and a zip code of 16802.

Once all goals are complete give a report of how it went
===============================================


Something to note, if you use that prompt, chrome will likly warn that the password you used is in a data breach. I always manually click this away and the test continues as normal. The AI meant learn to click it eventually but since it is not in the HTML code of the site it gets very confused, so I would manually click it away. It is possible that mentioning it in the prompt would allow it to click it away as well.